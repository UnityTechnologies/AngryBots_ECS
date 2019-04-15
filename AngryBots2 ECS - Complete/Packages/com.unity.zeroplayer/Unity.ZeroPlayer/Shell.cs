using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Unity.ZeroPlayer.NiceIO;

namespace JamSharp.Runtime
{
    public static class Shell
    {
        // Correct Windows argument quoting for Process.Start etc. (Mono implements a compatible scheme).
        // NOT the same as cmd.exe quoting or POSIX shell quoting!
        // https://blogs.msdn.microsoft.com/twistylittlepassagesallalike/2011/04/23/everyone-quotes-command-line-arguments-the-wrong-way/
        public static string WindowsArgQuote(string arg)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < arg.Length; ++i)
            {
                if (arg[i] == '"')
                {
                    for (int j = i - 1; j >= 0 && arg[j] == '\\'; --j)
                        sb.Append('\\');
                    sb.Append('\\');
                }
                sb.Append(arg[i]);
            }
            for (int j = arg.Length - 1; j >= 0 && arg[j] == '\\'; --j)
                sb.Append('\\');
            sb.Append('"');
            return sb.ToString();
        }

        public static void SplitInvocationStringToExecutableAndArgs(string commandLine, out string executable, out string arguments)
        {
            var commandAndArgs = new Regex(@"(""[^""]*""|\S*) (.*)").Matches(commandLine);
            if (commandAndArgs.Count > 0)
            {
                executable = commandAndArgs[0].Groups[1].Value;
                arguments = commandAndArgs[0].Groups[2].Value;
            }
            else
            {
                executable = commandLine;
                arguments = "";
            }
            if (string.IsNullOrWhiteSpace(executable))
                throw new ArgumentException($"Failed to extract executable from command line \"{commandLine}\".", nameof(commandLine));
        }

        /// <summary>
        /// Executes a program with a Unix-style argument array (matching the
        /// Main signature), instead of a single Windows-style argument string.
        /// Like the other methods in this class, the execution doesn't actually
        /// involve the shell (/bin/sh, cmd.exe or shell32.dll) in any way.
        /// </summary>
        public static ExecuteResult ExecuteArgv(string executable, params string[] args)
        {
            return Execute(executable, string.Join(" ", args.Select(WindowsArgQuote)));
        }

        public static ExecuteResult Execute(NPath filename, string arguments, Dictionary<string, string> envVars = null)
        {
            return Execute(filename.ToString(), arguments, envVars);
        }

        public static ExecuteResult Execute(string executable, string arguments, Dictionary<string, string> envVars = null)
        {
            var executeArgs = new ExecuteArgs()
            {
                Executable = executable,
                Arguments = arguments,
                EnvVars = envVars
            };
            return Execute(executeArgs);
        }

        public static ExecuteResult Execute(string commandLine, Dictionary<string, string> envVars = null)
        {
            var executeArgs = ExecuteArgs.FromCommandLine(commandLine);
            executeArgs.EnvVars = envVars;
            return Execute(executeArgs);
        }

        public static Task<ExecuteResult> ExecuteAsync(ExecuteArgs executeArgs)
        {
            return Task.Run(() => Execute(executeArgs));
        }

        private static void ReadOutput(StreamReader stream, StringBuilder stringBuilder)
        {
            char[] buffer = new char[1];
            try
            {
                while (true) // Do continue if stream was closed in order to read what's left.
                {
                    int numBytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (numBytesRead == 0)
                        break;
                    stringBuilder.Append(buffer);
                }
            }
            catch (IOException) {}
            catch (ObjectDisposedException) {}
        }

        public static ExecuteResult Execute(ExecuteArgs executeArgs, int timeoutMilliseconds = -1)
        {
            using (var p = NewProcess(executeArgs))
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                Thread stdoutCaptureThread = null;
                Thread stderrCaptureThread = null;

                var stopWatch = new Stopwatch();
                stopWatch.Start();

                p.Start();
                if (executeArgs.StdMode == StdMode.Capture)
                {
                    p.StandardInput.Close(); // stop interactive programs from reading stdin
                    stdoutCaptureThread = new Thread(() => ReadOutput(p.StandardOutput, stdout));
                    stdoutCaptureThread.Start();
                    stderrCaptureThread = new Thread(() => ReadOutput(p.StandardError, stderr));
                    stderrCaptureThread.Start();
                }

                bool reachedTimeout = false;
                if (timeoutMilliseconds != -1)
                {
                    if (!p.WaitForExit(timeoutMilliseconds))
                    {
                        reachedTimeout = true;
                        try
                        {
                            p.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                            // may occur if the process died after our timeout but before the Kill() call.
                        }
                    }
                }
                p.WaitForExit();

                stopWatch.Stop();

                int exitCode = p.ExitCode;

                // Closing the process should close the streams and therefore close the threads.
                stdoutCaptureThread?.Join();
                stderrCaptureThread?.Join();

                return new ExecuteResult
                {
                    ExecuteArgs = executeArgs,
                    ExitCode = exitCode,
                    StdOut = stdout.ToString().TrimEnd('\n', '\r'),
                    StdErr = stderr.ToString().TrimEnd('\n', '\r'),
                    Duration = TimeSpan.FromMilliseconds(stopWatch.ElapsedMilliseconds),
                    ReachedTimeout = reachedTimeout,
                };
            }
        }

        public static Process NewProcess(ExecuteArgs executeArgs)
        {
            var p = new Process
            {
                StartInfo =
                {
                    Arguments = executeArgs.Arguments,
                    CreateNoWindow = executeArgs.StdMode == StdMode.Capture,
                    UseShellExecute = false,
                    RedirectStandardOutput = executeArgs.StdMode == StdMode.Capture,
                    RedirectStandardInput = executeArgs.StdMode == StdMode.Capture,
                    RedirectStandardError = executeArgs.StdMode == StdMode.Capture,
                    FileName = executeArgs.Executable,
                    WorkingDirectory = executeArgs.WorkingDirectory
                }
            };
            if (executeArgs.StdMode == StdMode.Capture)
            {
                p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            }

            if (executeArgs.EnvVars != null)
            {
                foreach (var envVar in executeArgs.EnvVars)
                    p.StartInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
            }

            return p;
        }

        public class ExecuteArgs
        {
            public static ExecuteArgs FromCommandLine(string commandLine)
            {
                string executable;
                string arguments;
                SplitInvocationStringToExecutableAndArgs(commandLine, out executable, out arguments);
                return new ExecuteArgs() {Executable = executable, Arguments = arguments};
            }

            public string Executable { get; set; }
            public string Arguments { get; set; }
            public Dictionary<string, string> EnvVars { get; set; } = new Dictionary<string, string>();
            public string WorkingDirectory { get; set; }
            public StdMode StdMode { get; set; } = StdMode.Capture;

            public override string ToString() => $"{Executable} {Arguments}";
        }

        public enum StdMode
        {
            Stream,
            Capture
        }

        public class ExecuteResult
        {
            public string StdOut { get; set; }
            public string StdErr { get; set; }
            public int ExitCode { get; set; }
            public TimeSpan Duration { get; set; }
            public bool ReachedTimeout { get; set; } = false; // Whether the application was killed by timeout.

            public ExecuteArgs ExecuteArgs { get; set; }

            public ExecuteResult ThrowOnFailure()
            {
                if (ExitCode != 0)
                    throw new Exception($"Failed running {ExecuteArgs.Executable} {ExecuteArgs.Arguments}. ExitCode: {ExitCode}, Output was: {StdOut+StdErr}");
                return this;
            }
        }
    }
}
