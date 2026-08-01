/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public License.
 */

using System;
using System.Collections.Generic;
using SmartHopper.Infrastructure.AIProviders;
using SmartHopper.Infrastructure.Settings;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    internal sealed class CodexAccountProviderSettings : IAIProviderSettings
    {
        public bool EnableStreaming => false;

        public IEnumerable<SettingDescriptor> GetSettingDescriptors() => Array.Empty<SettingDescriptor>();

        public bool ValidateSettings(Dictionary<string, object> settings) => true;
    }
}
