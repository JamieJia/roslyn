﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using UIAutomationClient;
using AutomationElementIdentifiers = System.Windows.Automation.AutomationElementIdentifiers;
using AutomationProperty = System.Windows.Automation.AutomationProperty;
using ControlType = System.Windows.Automation.ControlType;

namespace Microsoft.VisualStudio.IntegrationTest.Utilities
{
    public static class DialogHelpers
    {
        /// <summary>
        /// Returns an <see cref="IUIAutomationElement"/> representing the open dialog with automation ID
        /// <paramref name="dialogAutomationId"/>.
        /// Throws an <see cref="InvalidOperationException"/> if an open dialog with that name cannot be
        /// found.
        /// </summary>
        public static IUIAutomationElement GetOpenDialogById(IntPtr visualStudioHWnd, string dialogAutomationId)
        {
            var dialogAutomationElement = FindDialogByAutomationId(visualStudioHWnd, dialogAutomationId, isOpen: true);
            if (dialogAutomationElement == null)
            {
                throw new InvalidOperationException($"Expected the {dialogAutomationId} dialog to be open, but it is not.");
            }

            return dialogAutomationElement;
        }

        public static IUIAutomationElement FindDialogByAutomationId(IntPtr visualStudioHWnd, string dialogAutomationId, bool isOpen, bool wait = true)
        {
            return Retry(
                () => FindDialogWorker(visualStudioHWnd, dialogAutomationId),
                stoppingCondition: automationElement => !wait || (isOpen ? automationElement != null : automationElement == null),
                delay: TimeSpan.FromMilliseconds(250));
        }

        /// <summary>
        /// Used to find legacy dialogs that don't have an AutomationId
        /// </summary>
        public static IUIAutomationElement FindDialogByName(IntPtr visualStudioHWnd, string dialogName, bool isOpen)
        {
            return Retry(
                () => FindDialogByNameWorker(visualStudioHWnd, dialogName),
                stoppingCondition: automationElement => isOpen ? automationElement != null : automationElement == null,
                delay: TimeSpan.FromMilliseconds(250));
        }

        /// <summary>
        /// Selects a specific item in a combo box.
        /// Note that combo box is found using its Automation ID, but the item is identified by name.
        /// </summary>
        public static void SelectComboBoxItem(IntPtr visualStudioHWnd, string dialogAutomationName, string comboBoxAutomationName, string itemText)
        {
            var dialogAutomationElement = GetOpenDialogById(visualStudioHWnd, dialogAutomationName);

            var comboBoxAutomationElement = dialogAutomationElement.FindDescendantByAutomationId(comboBoxAutomationName);
            comboBoxAutomationElement.Expand();

            var comboBoxItemAutomationElement = comboBoxAutomationElement.FindDescendantByName(itemText);
            comboBoxItemAutomationElement.Select();

            comboBoxAutomationElement.Collapse();
        }

        /// <summary>
        /// Selects a specific radio button from a dialog found by Id.
        /// </summary>
        public static void SelectRadioButton(IntPtr visualStudioHWnd, string dialogAutomationName, string radioButtonAutomationName)
        {
            var dialogAutomationElement = GetOpenDialogById(visualStudioHWnd, dialogAutomationName);

            var radioButton = dialogAutomationElement.FindDescendantByAutomationId(radioButtonAutomationName);
            radioButton.Select();
        }

        /// <summary>
        /// Sets the value of the specified element in the dialog.
        /// Used for setting the values of things like combo boxes and text fields.
        /// </summary>
        public static void SetElementValue(IntPtr visualStudioHWnd, string dialogAutomationId, string elementAutomationId, string value)
        {
            var dialogAutomationElement = GetOpenDialogById(visualStudioHWnd, dialogAutomationId);

            var control = dialogAutomationElement.FindDescendantByAutomationId(elementAutomationId);
            control.SetValue(value);
        }

        /// <summary>
        /// Presses the specified button.
        /// The button is identified using its automation ID; see <see cref="PressButtonWithName(IntPtr, string, string)"/>
        /// for the equivalent method that finds the button by name.
        /// </summary>
        public static void PressButton(IntPtr visualStudioHWnd, string dialogAutomationId, string buttonAutomationId)
        {
            var dialogAutomationElement = GetOpenDialogById(visualStudioHWnd, dialogAutomationId);

            var buttonAutomationElement = dialogAutomationElement.FindDescendantByAutomationId(buttonAutomationId);
            buttonAutomationElement.Invoke();
        }

        /// <summary>
        /// Presses the specified button.
        /// The button is identified using its name; see <see cref="PressButton(IntPtr, string, string)"/>
        /// for the equivalent methods that finds the button by automation ID.
        /// </summary>
        public static void PressButtonWithName(IntPtr visualStudioHWnd, string dialogAutomationId, string buttonName)
        {
            var dialogAutomationElement = GetOpenDialogById(visualStudioHWnd, dialogAutomationId);

            var buttonAutomationElement = dialogAutomationElement.FindDescendantByName(buttonName);
            buttonAutomationElement.Invoke();
        }

        /// <summary>
        /// Presses the specified button from a legacy dialog that has no AutomationId.
        /// The button is identified using its name; see <see cref="PressButton(IntPtr, string, string)"/>
        /// for the equivalent methods that finds the button by automation ID.
        /// </summary>
        public static void PressButtonWithNameFromDialogWithName(IntPtr visualStudioHWnd, string dialogName, string buttonName)
        {
            var dialogAutomationElement = FindDialogByName(visualStudioHWnd, dialogName, isOpen: true);

            var buttonAutomationElement = dialogAutomationElement.FindDescendantByName(buttonName);
            buttonAutomationElement.Invoke();
        }

        private static IUIAutomationElement FindDialogWorker(IntPtr visualStudioHWnd, string dialogAutomationName)
            => FindDialogByPropertyWorker(visualStudioHWnd, dialogAutomationName, AutomationElementIdentifiers.AutomationIdProperty);

        private static IUIAutomationElement FindDialogByNameWorker(IntPtr visualStudioHWnd, string dialogName)
            => FindDialogByPropertyWorker(visualStudioHWnd, dialogName, AutomationElementIdentifiers.NameProperty);

        private static IUIAutomationElement FindDialogByPropertyWorker(
            IntPtr visualStudioHWnd, 
            string propertyValue, 
            AutomationProperty nameProperty)
        {
            var vsAutomationElement = Helper.Automation.ElementFromHandle(visualStudioHWnd);

            var elementCondition = Helper.Automation.CreateAndConditionFromArray(
                new[]
                {
                    Helper.Automation.CreatePropertyCondition(nameProperty.Id, propertyValue),
                    Helper.Automation.CreatePropertyCondition(AutomationElementIdentifiers.ControlTypeProperty.Id, ControlType.Window.Id),
                });

            return vsAutomationElement.FindFirst(UIAutomationClient.TreeScope.TreeScope_Children, elementCondition);
        }

        private static T Retry<T>(Func<T> action, Func<T, bool> stoppingCondition, TimeSpan delay)
        {
            DateTime beginTime = DateTime.UtcNow;
            T retval = default(T);

            do
            {
                try
                {
                    retval = action();
                }
                catch (COMException)
                {
                    // Devenv can throw COMExceptions if it's busy when we make DTE calls.
                    Thread.Sleep(delay);
                    continue;
                }

                if (stoppingCondition(retval))
                {
                    return retval;
                }
                else
                {
                    Thread.Sleep(delay);
                }
            }
            while (true);
        }
    }
}
