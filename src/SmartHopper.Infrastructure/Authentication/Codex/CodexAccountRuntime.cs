/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public License.
 */

using System;
using System.Threading;
using Rhino;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    /// <summary>
    /// Process-wide owner for the account service. It never joins the AI provider registry.
    /// </summary>
    public static class CodexAccountRuntime
    {
        private static readonly Lazy<CodexAccountService> ServiceFactory = new (
            () => new CodexAccountService(
                new CodexExecutableLocator(),
                new CodexAppServerHost(),
                new CodexSystemBrowserLauncher()),
            LazyThreadSafetyMode.ExecutionAndPublication);

        private static int initialized;

        public static ICodexAccountService Service => ServiceFactory.Value;

        public static ICodexConversationTransport ConversationTransport => ServiceFactory.Value;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0)
            {
                return;
            }

            RhinoApp.Closing += OnRhinoClosing;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        private static void OnRhinoClosing(object? sender, EventArgs e)
        {
            if (ServiceFactory.IsValueCreated)
            {
                ServiceFactory.Value.BeginShutdown();
            }
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            if (ServiceFactory.IsValueCreated)
            {
                ServiceFactory.Value.Abort();
            }
        }
    }
}
