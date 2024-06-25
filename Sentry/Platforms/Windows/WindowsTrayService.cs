﻿using System.Drawing;
using System.Windows.Forms;
using OpenShock.SDK.CSharp.Hub;
using OpenShock.Sentry.Platforms.Windows;
using OpenShock.Sentry.Services;
using Application = Microsoft.Maui.Controls.Application;
using Image = System.Drawing.Image;

namespace OpenShock.Sentry.Platforms.Windows;

public class WindowsTrayService : ITrayService
{
    private readonly OpenShockHubClient _apiHubClient;

    /// <summary>
    /// Windows Tray Service
    /// </summary>
    /// <param name="apiHubClient"></param>
    public WindowsTrayService(OpenShockHubClient apiHubClient)
    {
        _apiHubClient = apiHubClient;
        
        _apiHubClient.Reconnecting += _ => HubStateChanged();
        _apiHubClient.Reconnected += _ => HubStateChanged();
        _apiHubClient.Closed += _ => HubStateChanged();
        _apiHubClient.Connected += _ => HubStateChanged();
    }
    
    private ToolStripLabel? _stateLabel = null;
    
    private Task HubStateChanged()
    {
        if (_stateLabel == null) return Task.CompletedTask;
        _stateLabel.Text = $"State: {_apiHubClient.State}";
        return Task.CompletedTask;
    }

    public void Initialize()
    {
        var tray = new NotifyIcon();
        tray.Icon = Icon.ExtractAssociatedIcon(@"Resources\sentry-icon.ico");
        tray.Text = "Sentry";

        var menu = new ContextMenuStrip();

        menu.Items.Add("Sentry", Image.FromFile(@"Resources\sentry-icon.ico"), OnMainClick);
        menu.Items.Add(new ToolStripSeparator());
        _stateLabel = new ToolStripLabel($"State: {_apiHubClient.State}");
        menu.Items.Add(_stateLabel);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Sentry", null, OnQuitClick);
        
        tray.ContextMenuStrip = menu;

        tray.Click += OnMainClick;

        tray.Visible = true;
    }

    private static void OnMainClick(object? sender, EventArgs eventArgs)
    {
        if (eventArgs is MouseEventArgs mouseEventArgs && mouseEventArgs.Button != MouseButtons.Left) return;

        var window = Application.Current?.Windows[0];
        var nativeWindow = window?.Handler?.PlatformView;
        if (nativeWindow == null) return;
        
        var appWindow = WindowUtils.GetAppWindow(nativeWindow);
        
        appWindow.ShowOnTop();
    }

    private static void OnQuitClick(object? sender, EventArgs eventArgs)
    {
        if (Application.Current != null)
        {
            Application.Current.Quit();
            return;
        }
        
        Environment.Exit(0);
    }
}