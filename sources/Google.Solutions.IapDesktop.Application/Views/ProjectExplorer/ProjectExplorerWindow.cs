﻿//
// Copyright 2020 Google LLC
//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using Google.Apis.Compute.v1.Data;
using Google.Solutions.Common.Locator;
using Google.Solutions.Common.Util;
using Google.Solutions.IapDesktop.Application.ObjectModel;
using Google.Solutions.IapDesktop.Application.Services.Adapters;
using Google.Solutions.IapDesktop.Application.Services.Integration;
using Google.Solutions.IapDesktop.Application.Services.Settings;
using Google.Solutions.IapDesktop.Application.Views.Dialog;
using Google.Solutions.IapDesktop.Application.Views.ProjectPicker;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

#pragma warning disable IDE1006 // Naming Styles

namespace Google.Solutions.IapDesktop.Application.Views.ProjectExplorer
{
    [ComVisible(false)]
    public partial class ProjectExplorerWindow : ToolWindow, IProjectExplorer
    {
        private readonly IMainForm mainForm;
        private readonly IEventService eventService;
        private readonly IJobService jobService;
        private readonly IProjectRepository projectInventoryService;
        private readonly IAuthorizationAdapter authService;
        private readonly IServiceProvider serviceProvider;
        private readonly ISessionBroker sessionBroker;

        private readonly ProjectExplorerViewModel viewModel;
        private readonly CloudNode rootNode = new CloudNode();

        public CommandContainer<IProjectExplorerNode> ContextMenuCommands { get; }
        public CommandContainer<IProjectExplorerNode> ToolbarCommands { get; }

        public ProjectExplorerWindow(IServiceProvider serviceProvider)
            : base(serviceProvider, DockState.DockLeft)
        {
            InitializeComponent();

            this.serviceProvider = serviceProvider;

            this.TabText = this.Text;

            //
            // This window is a singleton, so we never want it to be closed,
            // just hidden.
            //
            this.HideOnClose = true;

            this.vsToolStripExtender.SetStyle(
                this.toolStrip,
                VisualStudioToolStripExtender.VsVersion.Vs2015,
                this.vs2015LightTheme);

            this.treeView.Nodes.Add(this.rootNode);

            this.mainForm = serviceProvider.GetService<IMainForm>();
            this.eventService = serviceProvider.GetService<IEventService>();
            this.jobService = serviceProvider.GetService<IJobService>();
            this.projectInventoryService = serviceProvider.GetService<IProjectRepository>();
            this.authService = serviceProvider.GetService<IAuthorizationAdapter>();
            this.sessionBroker = serviceProvider.GetService<IGlobalSessionBroker>();

            this.eventService.BindAsyncHandler<ProjectAddedEvent>(OnProjectAdded);
            this.eventService.BindHandler<ProjectDeletedEvent>(OnProjectDeleted);
            this.eventService.BindHandler<SessionStartedEvent>(OnRdpSessionStarted);
            this.eventService.BindHandler<SessionEndedEvent>(OnRdpSessionEnded);

            this.ContextMenuCommands = new CommandContainer<IProjectExplorerNode>(
                this,
                this.contextMenu.Items,
                ToolStripItemDisplayStyle.ImageAndText,
                this.serviceProvider);
            this.ToolbarCommands = new CommandContainer<IProjectExplorerNode>(
                this,
                this.toolStrip.Items,
                ToolStripItemDisplayStyle.Image,
                this.serviceProvider);

            this.viewModel = new ProjectExplorerViewModel(
                serviceProvider.GetService<ApplicationSettingsRepository>());
            this.Disposed += (sender, args) =>
            {
                this.viewModel.Dispose();
            };

            //
            // Bind controls.
            //
            this.linuxInstancesToolStripMenuItem.BindProperty(
                c => c.Checked,
                this.viewModel,
                m => m.IsLinuxIncluded,
                this.Container);
            this.windowsInstancesToolStripMenuItem.BindProperty(
                c => c.Checked,
                this.viewModel,
                m => m.IsWindowsIncluded,
                this.Container);

            this.viewModel.PropertyChanged += (sender, args) =>
            {
                // Refresh tree, and show message on error.
                refreshButton_Click(sender, EventArgs.Empty);
            };
        }

        private void PopulateProjectNode(
            string projectId,
            IEnumerable<Instance> instances)
        {
            Debug.Assert(!this.InvokeRequired);

            // Narrow the list down by operating system.
            instances = instances.Where(i => i.IsWindowsInstance()
                ? this.viewModel.IsWindowsIncluded
                : this.viewModel.IsLinuxIncluded);

            var projectNode = this.rootNode.Nodes
                .Cast<ProjectNode>()
                .FirstOrDefault(n => n.Project.ProjectId == projectId);
            if (projectNode != null)
            {
                projectNode.Populate(
                    instances,
                    this.sessionBroker.IsConnected);
            }
            else
            {
                projectNode = new ProjectNode(projectId);
                projectNode.Populate(
                    instances,
                    this.sessionBroker.IsConnected);
                this.rootNode.Nodes.Add(projectNode);
            }

            this.rootNode.Expand();
        }

        private async Task<bool> AddProjectAsync()
        {
            await this.jobService.RunInBackground(
                    new JobDescription("Loading projects..."),
                    _ => this.authService.Authorization.Credential.GetAccessTokenForRequestAsync())
                .ConfigureAwait(true);

            // Show project picker
            var dialog = this.serviceProvider.GetService<IProjectPickerWindow>();
            string projectId = dialog.SelectProject(this);

            if (projectId == null)
            {
                // Cancelled.
                return false;
            }

            await this.projectInventoryService
                .AddProjectAsync(projectId)
                .ConfigureAwait(true);

            return true;
        }

        //---------------------------------------------------------------------
        // Context menu event handlers.
        //---------------------------------------------------------------------

        private void treeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                this.treeView.SelectedNode = e.Node;
            }
        }

        private async void refreshAllProjectsToolStripMenuItem_Click(object sender, EventArgs _)
        {
            try
            {
                await RefreshAllProjects().ConfigureAwait(true);
            }
            catch (Exception e) when (e.IsCancellation())
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Refreshing project failed", e);
            }
        }

        private async void refreshToolStripMenuItem_Click(object sender, EventArgs _)
        {
            try
            {
                if (this.treeView.SelectedNode is ProjectNode projectNode)
                {
                    await RefreshProject(projectNode.Project.ProjectId)
                        .ConfigureAwait(true);
                }
            }
            catch (Exception e) when (e.IsCancellation())
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Refreshing project failed", e);
            }
        }

        private async void unloadProjectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.treeView.SelectedNode is ProjectNode projectNode)
            {
                await this.projectInventoryService
                    .DeleteProjectAsync(projectNode.Project.ProjectId)
                    .ConfigureAwait(true);
            }
        }

        private void openInCloudConsoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var cloudConsoleService = this.serviceProvider.GetService<CloudConsoleService>();

            if (this.treeView.SelectedNode is VmInstanceNode vmInstanceNode)
            {
                cloudConsoleService.OpenInstanceDetails(vmInstanceNode.Instance);
            }
            else if (this.treeView.SelectedNode is ZoneNode zoneNode)
            {
                cloudConsoleService.OpenInstanceList(zoneNode.Zone);
            }
            else if (this.treeView.SelectedNode is ProjectNode projectNode)
            {
                cloudConsoleService.OpenInstanceList(projectNode.Project);
            }
        }

        private void configureIapAccessToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var cloudConsoleService = this.serviceProvider.GetService<CloudConsoleService>();

            if (this.treeView.SelectedNode is ProjectNode projectNode)
            {
                cloudConsoleService.ConfigureIapAccess(projectNode.Project.ProjectId);
            }
            else if (this.treeView.SelectedNode is ZoneNode zoneNode)
            {
                cloudConsoleService.ConfigureIapAccess(zoneNode.Zone.ProjectId);
            }
            else if (this.treeView.SelectedNode is VmInstanceNode vmInstanceNode)
            {
                cloudConsoleService.ConfigureIapAccess(vmInstanceNode.ProjectId);
            }
        }

        private void treeView_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            this.ContextMenuCommands.ExecuteDefaultCommand();
        }

        //---------------------------------------------------------------------
        // Tool bar event handlers.
        //---------------------------------------------------------------------

        private async void refreshButton_Click(object sender, EventArgs args)
        {
            try
            {
                await RefreshAllProjects().ConfigureAwait(true);
            }
            catch (Exception e) when (e.IsCancellation())
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Refreshing projects failed", e);
            }
        }

        private async void addButton_Click(object sender, EventArgs args)
        {
            try
            {
                await AddProjectAsync().ConfigureAwait(true);
            }
            catch (Exception e) when (e.IsCancellation())
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Adding project failed", e);
            }
        }

        //---------------------------------------------------------------------
        // Other Windows event handlers.
        //---------------------------------------------------------------------

        private async void ProjectExplorerWindow_Shown(object sender, EventArgs _)
        {
            try
            {
                await RefreshAllProjects().ConfigureAwait(true);

                if (this.rootNode.Nodes.Count == 0)
                {
                    // No projects in inventory yet - pop open the 'Add Project'
                    // dialog to get the user started.
                    await AddProjectAsync().ConfigureAwait(true);
                }
            }
            catch (Exception e) when (e.IsCancellation())
            {
                // Most likely, the user rejected to reauthorize. Quit the app.
                this.mainForm.Close();

            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "Loading projects failed", e);

                // Do not close the application, otherwise the user has no 
                // chance to remediate the situation by unloading the offending
                // project.
            }
        }

        private async void treeView_AfterSelect(object sender, TreeViewEventArgs args)
        {
            try
            {
                var selectedNode = (IProjectExplorerNode)args.Node;

                //
                // Update context menu state.
                //
                this.refreshToolStripMenuItem.Visible =
                    this.unloadProjectToolStripMenuItem.Visible = (selectedNode is ProjectNode);
                this.refreshAllProjectsToolStripMenuItem.Visible = (selectedNode is CloudNode);

                this.openInCloudConsoleToolStripMenuItem.Visible =
                    this.iapSeparatorToolStripMenuItem.Visible =
                    this.cloudConsoleSeparatorToolStripMenuItem.Visible =
                    this.configureIapAccessToolStripMenuItem.Visible =
                        (selectedNode is VmInstanceNode ||
                         selectedNode is ZoneNode ||
                         selectedNode is ProjectNode);

                // 
                // Handle dynamic menu items.
                //
                this.ContextMenuCommands.Context = selectedNode;
                this.ToolbarCommands.Context = selectedNode;

                //
                // Fire event.
                //
                await this.eventService
                    .FireAsync(new ProjectExplorerNodeSelectedEvent(selectedNode))
                    .ConfigureAwait(true);
            }
            catch (Exception e) when (e.IsCancellation())
            {
                // Ignore.
            }
            catch (Exception e)
            {
                this.serviceProvider
                    .GetService<IExceptionDialog>()
                    .Show(this, "An error occured", e);
            }
        }

        private void ProjectExplorerWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // NB. Hook KeyDown instead of KeyUp event to not interfere with 
            // child dialogs. With KeyUp, we'd get an event if a child dialog
            // is dismissed by pressing Enter.

            if (e.KeyCode == Keys.F5)
            {
                refreshAllProjectsToolStripMenuItem_Click(sender, EventArgs.Empty);
            }
            else if (e.KeyCode == Keys.Enter)
            {
                this.ContextMenuCommands.ExecuteDefaultCommand();
            }
            else
            {
                this.ContextMenuCommands.ExecuteCommandByKey(e.KeyCode);
            }
        }

        //---------------------------------------------------------------------
        // Service event handlers.
        //---------------------------------------------------------------------

        private async Task OnProjectAdded(ProjectAddedEvent e)
        {
            Debug.Assert(!this.InvokeRequired);

            await RefreshProject(e.ProjectId).ConfigureAwait(true);
        }

        private void OnProjectDeleted(ProjectDeletedEvent e)
        {
            Debug.Assert(!this.InvokeRequired);
            var node = this.rootNode.Nodes
                .Cast<ProjectNode>()
                .Where(p => p.Project.ProjectId == e.ProjectId)
                .FirstOrDefault();

            if (node != null)
            {
                // Remove corresponding node from tree.
                this.rootNode.Nodes.Remove(node);
            }
        }

        private void OnRdpSessionStarted(SessionStartedEvent e)
        {
            var node = (VmInstanceNode)TryFindNode(e.Instance);
            if (node != null)
            {
                node.IsConnected = true;
            }
        }


        private void OnRdpSessionEnded(SessionEndedEvent e)
        {
            var node = (VmInstanceNode)TryFindNode(e.Instance);
            if (node != null)
            {
                // Another connection might still be open, so re-check before
                // marking the node as not connected.
                node.IsConnected = this.sessionBroker.IsConnected(node.Instance);
            }
        }

        //---------------------------------------------------------------------
        // IProjectExplorer.
        //---------------------------------------------------------------------

        public async Task RefreshAllProjects()
        {
            Debug.Assert(!this.InvokeRequired);

            // Move selection to a "safe" spot.
            this.treeView.SelectedNode = this.rootNode;


            var failedProjects = new Dictionary<string, Exception>();

            var projectsAndInstances = await this.jobService.RunInBackground(
                new JobDescription("Loading projects..."),
                async token =>
                {
                    // NB. It is important to create a new adapter instance _within_ the job func
                    // so that when the job is retried due to reauth, we use a fresh instance.
                    using (var computeEngineAdapter = this.serviceProvider.GetService<IComputeEngineAdapter>())
                    {
                        var accumulator = new Dictionary<string, IEnumerable<Instance>>();

                        foreach (var project in await this.projectInventoryService
                            .ListProjectsAsync()
                            .ConfigureAwait(false))
                        {
                            try
                            {
                                accumulator[project.ProjectId] = await computeEngineAdapter
                                    .ListInstancesAsync(project.ProjectId, token)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception e) when (e.IsReauthError())
                            {
                                // Propagate reauth errors so that the reauth logic kicks in.
                                throw;
                            }
                            catch (Exception e)
                            {
                                // If one project fails to load, we should stil load the other onces.
                                failedProjects[project.ProjectId] = e;
                            }
                        }

                        return accumulator;
                    }
                }).ConfigureAwait(true);

            foreach (var entry in projectsAndInstances)
            {
                PopulateProjectNode(entry.Key, entry.Value);
            }

            if (failedProjects.Any())
            {
                // Add an (empty) project node so that the user can at least unload the project.
                foreach (string projectId in failedProjects.Keys)
                {
                    PopulateProjectNode(projectId, Enumerable.Empty<Instance>());
                }

                throw new AggregateException(
                    $"The following projects failed to refresh: {string.Join(", ", failedProjects.Keys)}",
                    failedProjects.Values.Cast<Exception>());
            }
        }

        public async Task RefreshProject(string projectId)
        {
            Debug.Assert(!this.InvokeRequired);

            using (var computeEngineAdapter = this.serviceProvider.GetService<IComputeEngineAdapter>())
            {
                var instances = await this.jobService.RunInBackground(
                        new JobDescription("Loading project inventory..."),
                        token => computeEngineAdapter.ListInstancesAsync(projectId, CancellationToken.None))
                    .ConfigureAwait(true);

                PopulateProjectNode(projectId, instances);
            }
        }

        public async Task ShowAddProjectDialogAsync()
        {
            // NB. The project explorer might be hidden and no project
            // might have been loaded yet.
            if (await AddProjectAsync().ConfigureAwait(true))
            {
                // Show the window. That might kick of an asynchronous
                // Refresh if the window previously was not visible.
                ShowWindow();
            }
        }

        public IProjectExplorerInstanceNode TryFindNode(InstanceLocator reference)
        {
            return this.rootNode.Nodes
                .OfType<ProjectNode>()
                .Where(p => p.Project.ProjectId == reference.ProjectId)
                .SelectMany(p => p.Nodes.Cast<ZoneNode>())
                .Where(z => z.Zone.Name == reference.Zone)
                .SelectMany(z => z.Nodes.Cast<VmInstanceNode>())
                .FirstOrDefault(vm => vm.InstanceName == reference.Name); ;
        }

        public IProjectExplorerNode SelectedNode
        {
            get => (this.treeView.SelectedNode as IProjectExplorerNode) ?? this.rootNode;
        }
    }
}

