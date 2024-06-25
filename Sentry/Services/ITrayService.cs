﻿namespace OpenShock.Sentry.Services;

public interface ITrayService
{
    /// <summary>
    /// Setup the tray icon and make it visible
    /// </summary>
    public void Initialize();
}