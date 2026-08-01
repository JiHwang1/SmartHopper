/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    public sealed class CodexExecutableProbeResult
    {
        public bool Success { get; init; }

        public string? Version { get; init; }
    }

    public interface ICodexExecutableProbe
    {
        Task<CodexExecutableProbeResult> ProbeAsync(string executablePath, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Locates Codex from an explicit path or PATH and verifies it with <c>codex --version</c>.
    /// </summary>
    public sealed class CodexExecutableLocator : ICodexExecutableLocator
    {
        private readonly bool isWindows;
        private readonly string pathValue;
        private readonly Func<string, bool> fileExists;
        private readonly ICodexExecutableProbe probe;

        public CodexExecutableLocator()
            : this(
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
                File.Exists,
                new CodexExecutableProbe())
        {
        }

        public CodexExecutableLocator(
            bool isWindows,
            string pathValue,
            Func<string, bool> fileExists,
            ICodexExecutableProbe probe)
        {
            this.isWindows = isWindows;
            this.pathValue = pathValue ?? string.Empty;
            this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
            this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        }

        public async Task<CodexExecutableInfo> LocateAsync(string? configuredPath, CancellationToken cancellationToken = default)
        {
            var candidates = this.BuildCandidates(configuredPath);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!this.fileExists(candidate))
                {
                    continue;
                }

                CodexExecutableProbeResult result;
                try
                {
                    result = await this.probe.ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    continue;
                }

                if (result.Success)
                {
                    return new CodexExecutableInfo
                    {
                        IsAvailable = true,
                        Path = candidate,
                        Version = result.Version,
                    };
                }
            }

            var explicitPathInvalid = !string.IsNullOrWhiteSpace(configuredPath);
            return new CodexExecutableInfo
            {
                IsAvailable = false,
                UserMessage = explicitPathInvalid
                    ? "지정한 Codex CLI를 실행할 수 없습니다. 경로를 확인하세요."
                    : "Codex CLI를 찾지 못했습니다. Codex를 설치하거나 실행 파일 경로를 지정하세요.",
            };
        }

        private IReadOnlyList<string> BuildCandidates(string? configuredPath)
        {
            var result = new List<string>();
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                result.Add(this.NormalizePath(configuredPath.Trim().Trim('"')));
                return result;
            }

            var names = this.isWindows ? new[] { "codex.exe", "codex.cmd" } : new[] { "codex" };
            var separator = this.isWindows ? ';' : ':';
            foreach (var directory in this.pathValue.Split(separator))
            {
                var trimmed = directory.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                foreach (var name in names)
                {
                    var candidate = this.isWindows
                        ? Path.GetFullPath(Path.Combine(trimmed, name))
                        : trimmed.TrimEnd('/') + "/" + name;
                    if (!result.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(candidate);
                    }
                }
            }

            return result;
        }

        private string NormalizePath(string path)
        {
            return this.isWindows ? Path.GetFullPath(path) : path;
        }
    }

    /// <summary>
    /// Real executable probe used outside tests.
    /// </summary>
    public sealed class CodexExecutableProbe : ICodexExecutableProbe
    {
        public async Task<CodexExecutableProbeResult> ProbeAsync(string executablePath, CancellationToken cancellationToken = default)
        {
            using var process = new Process
            {
                StartInfo = CodexProcessStartInfo.Create(executablePath, new[] { "--version" }, null),
            };

            if (!process.Start())
            {
                return new CodexExecutableProbeResult();
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new CodexExecutableProbeResult();
            }

            var output = (await stdout.ConfigureAwait(false)).Trim();
            var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return new CodexExecutableProbeResult
            {
                Success = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(firstLine),
                Version = firstLine != null && firstLine.Length <= 100 ? firstLine : null,
            };
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>
    /// Builds shell-free process starts, except for Windows command shims which require cmd.exe.
    /// The shim path is validated and all app-server arguments are fixed by SmartHopper.
    /// </summary>
    public static class CodexProcessStartInfo
    {
        private static readonly char[] UnsafeCommandPathCharacters = { '&', '|', '<', '>', '^', '%', '!', '\r', '\n' };
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static ProcessStartInfo Create(string executablePath, IReadOnlyList<string> arguments, string? codexHome)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("Codex executable path is required.", nameof(executablePath));
            }

            var fullPath = Path.GetFullPath(executablePath);
            var isCommandShim = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                string.Equals(Path.GetExtension(fullPath), ".cmd", StringComparison.OrdinalIgnoreCase);

            ProcessStartInfo startInfo;
            if (isCommandShim)
            {
                if (fullPath.IndexOfAny(UnsafeCommandPathCharacters) >= 0)
                {
                    throw new InvalidOperationException("The Codex command shim path contains unsupported characters.");
                }

                var commandArguments = string.Join(" ", arguments.Select(QuoteFixedArgument));
                startInfo = new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = $"/d /s /c \"\"{fullPath}\" {commandArguments}\"",
                };
            }
            else
            {
                startInfo = new ProcessStartInfo { FileName = fullPath };
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }
            }

            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardInputEncoding = Utf8NoBom;
            startInfo.StandardOutputEncoding = Utf8NoBom;
            startInfo.StandardErrorEncoding = Utf8NoBom;
            if (!string.IsNullOrWhiteSpace(codexHome))
            {
                startInfo.Environment["CODEX_HOME"] = Path.GetFullPath(codexHome.Trim());
            }

            return startInfo;
        }

        private static string QuoteFixedArgument(string argument)
        {
            if (argument.Any(ch => char.IsWhiteSpace(ch) || ch == '"' || UnsafeCommandPathCharacters.Contains(ch)))
            {
                throw new InvalidOperationException("Unsupported Codex argument.");
            }

            return argument;
        }
    }
}
