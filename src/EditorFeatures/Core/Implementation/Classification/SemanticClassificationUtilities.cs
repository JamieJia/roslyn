﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Editor.Tagging;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PersistentStorage;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.Classification
{
    internal static class SemanticClassificationUtilities
    {
        /// <summary>
        /// Mapping from workspaces to a task representing when they are fully loaded.  While this task is not complete,
        /// the workspace is still loading.  Once complete the workspace is loaded.  We store the task around, instead
        /// of just awaiting <see cref="IWorkspaceStatusService.IsFullyLoadedAsync"/> as actually awaiting that call
        /// takes non-neglible time (upwards of several hundred ms, to a second), whereas we can actually just use the
        /// status of the <see cref="IWorkspaceStatusService.WaitUntilFullyLoadedAsync"/> task to know when we have
        /// actually transitioned to a loaded state.
        /// </summary>
        private static readonly ConditionalWeakTable<Workspace, Task> s_workspaceToFullyLoadedStateTask =
            new ConditionalWeakTable<Workspace, Task>();

        public static async Task ProduceTagsAsync(
            TaggerContext<IClassificationTag> context,
            DocumentSnapshotSpan spanToTag,
            IClassificationService classificationService,
            ClassificationTypeMap typeMap)
        {
            var document = spanToTag.Document;
            if (document == null)
            {
                return;
            }

            var classified = await TryClassifyContainingMemberSpanAsync(
                    context, spanToTag, classificationService, typeMap).ConfigureAwait(false);
            if (classified)
            {
                return;
            }

            // We weren't able to use our specialized codepaths for semantic classifying. 
            // Fall back to classifying the full span that was asked for.
            await ClassifySpansAsync(
                context, spanToTag, classificationService, typeMap).ConfigureAwait(false);
        }

        private static async Task<bool> TryClassifyContainingMemberSpanAsync(
            TaggerContext<IClassificationTag> context,
            DocumentSnapshotSpan spanToTag,
            IClassificationService classificationService,
            ClassificationTypeMap typeMap)
        {
            var range = context.TextChangeRange;
            if (range == null)
            {
                // There was no text change range, we can't just reclassify a member body.
                return false;
            }

            // there was top level edit, check whether that edit updated top level element
            var document = spanToTag.Document;
            if (!document.SupportsSyntaxTree)
            {
                return false;
            }

            var cancellationToken = context.CancellationToken;

            var lastSemanticVersion = (VersionStamp?)context.State;
            if (lastSemanticVersion != null)
            {
                var currentSemanticVersion = await document.Project.GetDependentSemanticVersionAsync(cancellationToken).ConfigureAwait(false);
                if (lastSemanticVersion.Value != currentSemanticVersion)
                {
                    // A top level change was made.  We can't perform this optimization.
                    return false;
                }
            }

            var service = document.GetLanguageService<ISyntaxFactsService>();

            // perf optimization. Check whether all edits since the last update has happened within
            // a member. If it did, it will find the member that contains the changes and only refresh
            // that member.  If possible, try to get a speculative binder to make things even cheaper.

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var changedSpan = new TextSpan(range.Value.Span.Start, range.Value.NewLength);
            var member = service.GetContainingMemberDeclaration(root, changedSpan.Start);
            if (member == null || !member.FullSpan.Contains(changedSpan))
            {
                // The edit was not fully contained in a member.  Reclassify everything.
                return false;
            }

            var subTextSpan = service.GetMemberBodySpanForSpeculativeBinding(member);
            if (subTextSpan.IsEmpty)
            {
                // Wasn't a member we could reclassify independently.
                return false;
            }

            var subSpan = subTextSpan.Contains(changedSpan) ? subTextSpan.ToSpan() : member.FullSpan.ToSpan();

            var subSpanToTag = new DocumentSnapshotSpan(spanToTag.Document,
                new SnapshotSpan(spanToTag.SnapshotSpan.Snapshot, subSpan));

            // re-classify only the member we're inside.
            await ClassifySpansAsync(
                context, subSpanToTag, classificationService, typeMap).ConfigureAwait(false);
            return true;
        }

        private static async Task ClassifySpansAsync(
            TaggerContext<IClassificationTag> context,
            DocumentSnapshotSpan spanToTag,
            IClassificationService classificationService,
            ClassificationTypeMap typeMap)
        {
            try
            {
                var document = spanToTag.Document;
                var snapshotSpan = spanToTag.SnapshotSpan;
                var snapshot = snapshotSpan.Snapshot;

                var cancellationToken = context.CancellationToken;
                using (Logger.LogBlock(FunctionId.Tagger_SemanticClassification_TagProducer_ProduceTags, cancellationToken))
                {
                    var classifiedSpans = ClassificationUtilities.GetOrCreateClassifiedSpanList();

                    await AddSemanticClassificationsAsync(
                        document, snapshotSpan.Span.ToTextSpan(), classificationService, classifiedSpans, cancellationToken: cancellationToken).ConfigureAwait(false);

                    ClassificationUtilities.Convert(typeMap, snapshotSpan.Snapshot, classifiedSpans, context.AddTag);
                    ClassificationUtilities.ReturnClassifiedSpanList(classifiedSpans);

                    var version = await document.Project.GetDependentSemanticVersionAsync(cancellationToken).ConfigureAwait(false);

                    // Let the context know that this was the span we actually tried to tag.
                    context.SetSpansTagged(SpecializedCollections.SingletonEnumerable(spanToTag));
                    context.State = version;
                }
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        private static async Task AddSemanticClassificationsAsync(
            Document document,
            TextSpan textSpan,
            IClassificationService classificationService,
            List<ClassifiedSpan> classifiedSpans,
            CancellationToken cancellationToken)
        {
            var workspace = document.Project.Solution.Workspace;
            var fullyLoadedStateTask = s_workspaceToFullyLoadedStateTask.GetValue(
                workspace, w =>
                {
                    var workspaceLoadedService = workspace.Services.GetRequiredService<IWorkspaceStatusService>();
                    return workspaceLoadedService.WaitUntilFullyLoadedAsync(CancellationToken.None);
                });

            // If we're not fully loaded try to read from the cache instead so that classifications appear up to date.
            // New code will not be semantically classified, but will eventually when the project fully loads.
            var isFullyLoaded = fullyLoadedStateTask.IsCompleted;
            if (await TryAddSemanticClassificationsFromCacheAsync(document, textSpan, classifiedSpans, isFullyLoaded, cancellationToken).ConfigureAwait(false))
                return;

            await classificationService.AddSemanticClassificationsAsync(
                document, textSpan, classifiedSpans, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> TryAddSemanticClassificationsFromCacheAsync(
            Document document,
            TextSpan textSpan,
            List<ClassifiedSpan> classifiedSpans,
            bool isFullyLoaded,
            CancellationToken cancellationToken)
        {
            // Don't use the cache if we're fully loaded.  We should just compute values normally.
            if (isFullyLoaded)
                return false;

            var semanticCacheService = document.Project.Solution.Workspace.Services.GetService<ISemanticClassificationCacheService>();
            if (semanticCacheService == null)
                return false;

            var checksums = await document.State.GetStateChecksumsAsync(cancellationToken).ConfigureAwait(false);
            var checksum = checksums.Text;

            var result = await semanticCacheService.GetCachedSemanticClassificationsAsync(
                (DocumentKey)document, textSpan, checksum, cancellationToken).ConfigureAwait(false);
            if (result.IsDefault)
                return false;

            classifiedSpans.AddRange(result);
            return true;
        }
    }
}
