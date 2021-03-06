﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Internal.VisualStudio.PlatformUI;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Project;
using Microsoft.PythonTools.Wpf;
using Microsoft.VisualStudio.Shell.Interop;

namespace Microsoft.PythonTools.Environments {
    internal partial class AddEnvironmentDialog : ModernDialog, IDisposable {
        public static readonly ICommand MoreInfo = new RoutedCommand();

        public AddEnvironmentDialog(IEnumerable<EnvironmentViewBase> pages, EnvironmentViewBase selected) {
            if (pages == null) {
                throw new ArgumentNullException(nameof(pages));
            }

            DataContext = new AddEnvironmentView(pages, selected);
            InitializeComponent();
        }

        public AddEnvironmentView View => (AddEnvironmentView)DataContext;

        enum PageKind {
            CondaEnvironment,
            VirtualEnvironment,
            ExistingEnvironment,
            InstalledEnvironment,
        }

        public static async Task ShowAddEnvironmentDialogAsync(
            IServiceProvider site,
            PythonProjectNode project,
            string existingCondaEnvName = null,
            string environmentYmlPath = null,
            string requirementsTxtPath = null,
            CancellationToken ct = default(CancellationToken)
        ) {
            // For now default to the first tab (virtual environment)
            await ShowAddVirtualEnvironmentDialogAsync(
                site,
                project,
                existingCondaEnvName,
                environmentYmlPath,
                requirementsTxtPath,
                ct
            );
        }

        public static async Task ShowAddVirtualEnvironmentDialogAsync(
            IServiceProvider site,
            PythonProjectNode project,
            string existingCondaEnvName = null,
            string environmentYmlPath = null,
            string requirementsTxtPath = null,
            CancellationToken ct = default(CancellationToken)
        ) {
            await ShowDialogAsync(
                PageKind.VirtualEnvironment,
                site,
                project,
                existingCondaEnvName,
                environmentYmlPath,
                requirementsTxtPath,
                ct
            );
        }

        public static async Task ShowAddCondaEnvironmentDialogAsync(
            IServiceProvider site,
            PythonProjectNode project,
            string existingCondaEnvName = null,
            string environmentYmlPath = null,
            string requirementsTxtPath = null,
            CancellationToken ct = default(CancellationToken)
        ) {
            await ShowDialogAsync(
                PageKind.CondaEnvironment,
                site,
                project,
                existingCondaEnvName,
                environmentYmlPath,
                requirementsTxtPath,
                ct
            );
        }

        public static async Task ShowAddExistingEnvironmentDialogAsync(
            IServiceProvider site,
            PythonProjectNode project,
            string existingCondaEnvName = null,
            string environmentYmlPath = null,
            string requirementsTxtPath = null,
            CancellationToken ct = default(CancellationToken)
        ) {
            await ShowDialogAsync(
                PageKind.ExistingEnvironment,
                site,
                project,
                existingCondaEnvName,
                environmentYmlPath,
                requirementsTxtPath,
                ct
            );
        }

        private static async Task ShowDialogAsync(
            PageKind activePage,
            IServiceProvider site,
            PythonProjectNode project,
            string existingCondaEnvName = null,
            string environmentYmlPath = null,
            string requirementsTxtPath = null,
            CancellationToken ct = default(CancellationToken)
        ) {
            if (site == null) {
                throw new ArgumentNullException(nameof(site));
            }

            ProjectView[] projectViews;
            ProjectView selectedProjectView;

            try {
                var sln = (IVsSolution)site.GetService(typeof(SVsSolution));
                var projects = sln?.EnumerateLoadedPythonProjects().ToArray() ?? new PythonProjectNode[0];

                projectViews = projects
                    .Select((projectNode) => new ProjectView(projectNode))
                    .ToArray();

                selectedProjectView = projectViews.SingleOrDefault(pv => pv.Node == project);
            } catch (InvalidOperationException ex) {
                Debug.Fail(ex.ToUnhandledExceptionMessage(typeof(AddEnvironmentDialog)));
                projectViews = new ProjectView[0];
                selectedProjectView = null;
            }

            if (selectedProjectView != null) {
                if (existingCondaEnvName != null) {
                    selectedProjectView.MissingCondaEnvName = existingCondaEnvName;
                }

                if (environmentYmlPath != null) {
                    selectedProjectView.EnvironmentYmlPath = environmentYmlPath;
                }

                if (requirementsTxtPath != null) {
                    selectedProjectView.RequirementsTxtPath = requirementsTxtPath;
                }
            }

            var addVirtualView = new AddVirtualEnvironmentView(
                site,
                projectViews,
                selectedProjectView
            );

            var addCondaView = new AddCondaEnvironmentView(
                site,
                projectViews,
                selectedProjectView
            );

            var addExistingView = new AddExistingEnvironmentView(
                site,
                projectViews,
                selectedProjectView
            );

            var addInstalledView = new AddInstalledEnvironmentView(
                site,
                projectViews,
                selectedProjectView
            );

            EnvironmentViewBase activeView;
            switch (activePage) {
                case PageKind.VirtualEnvironment:
                    activeView = addVirtualView;
                    break;
                case PageKind.CondaEnvironment:
                    activeView = addCondaView;
                    break;
                case PageKind.ExistingEnvironment:
                    activeView = addExistingView;
                    break;
                case PageKind.InstalledEnvironment:
                    activeView = addInstalledView;
                    break;
                default:
                    Debug.Assert(false, string.Format("Unknown page kind '{0}'", activePage));
                    activeView = null;
                    break;
            }

            using (var dlg = new AddEnvironmentDialog(
                new EnvironmentViewBase[] {
                    addVirtualView,
                    addCondaView,
                    addExistingView,
                    addInstalledView,
                },
                activeView
            )) {
                try {
                    WindowHelper.ShowModal(dlg);
                } catch (Exception) {
                    dlg.Close();
                    throw;
                }

                if (dlg.DialogResult ?? false) {
                    var view = dlg.View.PagesView.CurrentItem as EnvironmentViewBase;
                    Debug.Assert(view != null);
                    if (view != null) {
                        try {
                            await view.ApplyAsync();
                        } catch (Exception ex) when (!ex.IsCriticalException()) {
                            Debug.Fail(ex.ToUnhandledExceptionMessage(typeof(AddEnvironmentDialog)), Strings.ProductTitle);
                        }
                    }
                }
            }
        }

        private void OkClick(object sender, System.Windows.RoutedEventArgs e) {
            var view = View.PagesView.CurrentItem as EnvironmentViewBase;
            Debug.Assert(view != null);
            if (view != null) {
                var errors = view.GetAllErrors();
                if (errors.Any()) {
                    MessageBox.Show(
                        Strings.AddEnvironmentValidationErrors.FormatUI(string.Join(Environment.NewLine + Environment.NewLine, errors)),
                        Strings.ProductTitle,
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                    return;
                }
            }

            DialogResult = true;
            Close();
        }

        private void CancelClick(object sender, System.Windows.RoutedEventArgs e) {
            DialogResult = false;
            Close();
        }

        private void MoreInfo_CanExecute(object sender, CanExecuteRoutedEventArgs e) {
            e.CanExecute = true;
        }

        private void MoreInfo_Executed(object sender, ExecutedRoutedEventArgs e) {
            Process.Start(PythonToolsPackage.InterpreterHelpUrl)?.Dispose();
        }

        public void Dispose() {
            View.Dispose();
        }
    }
}
