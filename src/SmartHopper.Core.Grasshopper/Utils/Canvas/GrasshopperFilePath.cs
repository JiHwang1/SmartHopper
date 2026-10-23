/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 */

using System;
using System.IO;

namespace SmartHopper.Core.Grasshopper.Utils.Canvas
{
    /// <summary>Validates document file paths without depending on Rhino or Grasshopper.</summary>
    public static class GrasshopperFilePath
    {
        /// <summary>Validates and normalizes an absolute .gh or .ghx path.</summary>
        public static bool TryNormalize(string? path, bool mustExist, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "A file path is required.";
                return false;
            }

            try
            {
                if (!Path.IsPathRooted(path))
                {
                    error = "The file path must be absolute.";
                    return false;
                }

                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                error = $"Invalid file path: {ex.Message}";
                return false;
            }

            var extension = Path.GetExtension(fullPath);
            if (!string.Equals(extension, ".gh", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".ghx", StringComparison.OrdinalIgnoreCase))
            {
                error = "Only Grasshopper files (.gh or .ghx) are supported.";
                return false;
            }

            if (mustExist && !File.Exists(fullPath))
            {
                error = $"File not found: {fullPath}";
                return false;
            }

            return true;
        }
    }
}
