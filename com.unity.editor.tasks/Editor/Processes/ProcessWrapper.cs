namespace Unity.Editor.Tasks
{
	using System;
	using System.Collections.Generic;
	using System.ComponentModel;
	using System.Diagnostics;
	using System.IO;
	using System.Text;
	using System.Threading;
	using Extensions;
	using Helpers;
	using Logging;

	public abstract class BaseProcessWrapper : IDisposable
	{
		public ProcessStartInfo StartInfo { get; }
		public StreamWriter Input { get; protected set; }
		public int ProcessId { get; protected set; }
		public int ExitCode { get; protected set; }
		public bool HasExited { get; protected set; }

		protected BaseProcessWrapper(ProcessStartInfo startInfo)
		{
			startInfo.EnsureNotNull(nameof(startInfo));
			this.StartInfo = startInfo;
		}

		public virtual void Detach() { }

		public abstract void Run();
		public abstract void Stop(bool dontWait = false);

		#region IDisposable Support

		protected virtual void Dispose(bool disposing)
		{ }

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}
		#endregion
	}

	public class ProcessWrapper : BaseProcessWrapper
	{
		private readonly List<string> errors = new List<string>();
		private readonly Action onEnd;
		private readonly Action<Exception, string> onError;
		private readonly Action onStart;
		private readonly IOutputProcessor outputProcessor;
		private readonly ManualResetEventSlim stopEvent = new ManualResetEventSlim(false);
		private readonly string taskName;
		private readonly CancellationTokenSource cts;

		private ILogging logger;
		private bool detached;
		private DateTimeOffset lastOutput;
		private Exception thrownException;
		private AutoResetEvent gotOutput;
		// set when the async readers hit end-of-stream (e.Data == null), so we know all output is in
		// without having to wait for a period of silence
		private volatile bool outputClosed;
		private volatile bool errorClosed;

		private bool StreamsClosed => outputClosed && errorClosed;

		public ProcessWrapper(string taskName, ProcessStartInfo startInfo,
			IOutputProcessor outputProcessor,
			Action onStart, Action onEnd, Action<Exception, string> onError,
			CancellationToken token = default)
			: base(startInfo)
		{
			this.taskName = taskName;
			this.outputProcessor = outputProcessor;
			this.onStart = onStart;
			this.onEnd = onEnd;
			this.onError = onError;
			cts = CancellationTokenSource.CreateLinkedTokenSource(token);
			Process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
		}

		public override void Detach()
		{
			detached = true;
			stopEvent.Set();
		}

		public override void Run()
		{
			lastOutput = DateTimeOffset.UtcNow;
			thrownException = null;
			gotOutput = new AutoResetEvent(false);
			outputClosed = !StartInfo.RedirectStandardOutput;
			errorClosed = !StartInfo.RedirectStandardError;

			if (StartInfo.RedirectStandardError)
				Process.ErrorDataReceived += OnErrorDataReceived;

			if (StartInfo.RedirectStandardOutput)
				Process.OutputDataReceived += OnOutputDataReceived;

			Process.Exited += OnExited;

			try
			{
				Logger.Trace($"Running '{StartInfo.FileName} {StartInfo.Arguments}' in '{StartInfo.WorkingDirectory}'");

				cts.Token.ThrowIfCancellationRequested();

				Process.Start();

				ProcessId = Process.Id;

				if (StartInfo.RedirectStandardInput)
					Input = new StreamWriter(Process.StandardInput.BaseStream, new UTF8Encoding(false));
				if (StartInfo.RedirectStandardError)
					Process.BeginErrorReadLine();
				if (StartInfo.RedirectStandardOutput)
					Process.BeginOutputReadLine();

				onStart?.Invoke();

				if (StartInfo.CreateNoWindow)
				{
					stopEvent.Wait(cts.Token);
					if (!detached)
					{
						var exited = WaitForExit(500);
						if (exited)
						{
							// process is done; drain any remaining output
							WaitForStreamsClosed();
						}
						else if (cts.IsCancellationRequested)
						// if we're exiting
						{
							Stop(true);
							ExitCode = Process.ExitCode;
							cts.Token.ThrowIfCancellationRequested();
							throw new ProcessException(-2, "Process timed out");
						}

						if (Process.ExitCode != 0 && errors.Count > 0)
						{
							thrownException = new ProcessException(Process.ExitCode, string.Join(Environment.NewLine, errors.ToArray()));
						}
					}
				}
			}
			catch (Exception ex)
			{
				HasExited = true;
				try
				{
					HasExited = Process.HasExited;
				}
				catch { }

				if (!HasExited)
				{
					Stop(true);
				}

				var errorCode = -42;
				if (ex is Win32Exception)
					errorCode = ((Win32Exception)ex).NativeErrorCode;

				StringBuilder sb = new StringBuilder();
				sb.AppendLine($"Error code {errorCode}");
				sb.AppendLine(ex.Message);
				if (StartInfo.Arguments.Contains("-credential"))
					sb.AppendLine($"'{StartInfo.FileName} {taskName}'");
				else
					sb.AppendLine($"'{StartInfo.FileName} {StartInfo.Arguments}'");
				if (errorCode == 2)
					sb.AppendLine("The system cannot find the file specified.");
				sb.AppendLine($"Working directory: {StartInfo.WorkingDirectory}");

				//foreach (string env in StartInfo.EnvironmentVariables.Keys)
				//{
				//	sb.AppendFormat("{0}:{1}", env, StartInfo.EnvironmentVariables[env]);
				//	sb.AppendLine();
				//}

				thrownException = new ProcessException(errorCode, sb.ToString(), ex);
			}

			if (!detached)
			{
				try
				{
					if (HasExited)
					{
						ExitCode = Process.ExitCode;
						Process.Close();
					}
				}
				catch
				{ }
			}

			if (thrownException != null || errors.Count > 0)
				onError?.Invoke(thrownException, string.Join(Environment.NewLine, errors.ToArray()));

			onEnd?.Invoke();
		}

		private void OnExited(object sender, EventArgs e)
		{
			try
			{
				WaitForStreamsClosed();
				HasExited = true;
				stopEvent.Set();
			}
			catch (Exception ex)
			{
				errors.Add(ex.GetExceptionMessageShort());
			}
		}

		private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
		{
			try
			{
				//if (e.Data != null)
				//{
				//    Logger.Trace("ErrorData \"" + (e.Data == null ? "'null'" : e.Data) + "\"");
				//}

				lastOutput = DateTimeOffset.UtcNow;
				if (e.Data != null)
					errors.Add(e.Data.TrimEnd('\r', '\n'));
				else
					errorClosed = true;
				gotOutput.Set();
			}
			catch (Exception ex)
			{
				errors.Add(ex.GetExceptionMessageShort());
			}
		}

		private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
		{
			try
			{
				lastOutput = DateTimeOffset.UtcNow;
				if (e.Data != null)
				{
					outputProcessor.Process(e.Data.TrimEnd('\r', '\n'));
				}
				else
				{
					outputProcessor.Process(null);
				}
			}
			catch (Exception ex)
			{
				errors.Add(ex.GetExceptionMessageShort());
			}
			finally
			{
				// flag and signal only after the processor has seen the line, so a waiter never
				// returns before the last line has been handled
				if (e.Data == null)
					outputClosed = true;
				gotOutput.Set();
			}
		}

		/// <summary>
		/// Returns as soon as both redirected streams have reported end-of-stream. Falls back to
		/// returning after 100ms without output, in case a child process (credential helper, ssh)
		/// inherited the pipe and keeps it open after git itself has exited.
		/// </summary>
		private void WaitForStreamsClosed()
		{
			while (!StreamsClosed && !cts.IsCancellationRequested && gotOutput.WaitOne(100))
			{ }
		}

		public override void Stop(bool dontWait = false)
		{
			if (disposed) return;
			try
			{
				if (StartInfo.RedirectStandardError)
				{
					Process.ErrorDataReceived -= OnErrorDataReceived;
					Process.CancelErrorRead();
				}
				if (StartInfo.RedirectStandardOutput)
				{
					Process.OutputDataReceived -= OnOutputDataReceived;
					Process.CancelOutputRead();
				}
				if (!Process.HasExited && StartInfo.RedirectStandardInput)
					Input.WriteLine("\x3");
				if (Process.HasExited)
					return;
			}
			catch
			{ }

			bool waitSucceeded = false;
			try
			{
				if (!dontWait)
				{
					waitSucceeded = Process.WaitForExit(500);
				}
			}
			catch (Exception ex)
			{
				Logger.Trace(ex);
			}

			try
			{
				if (!waitSucceeded)
				{
					Process.Kill();
					waitSucceeded = Process.WaitForExit(100);
				}
			}
			catch (Exception ex)
			{
				Logger.Trace(ex);
			}
			stopEvent.Set();
		}

		private bool WaitForExit(int milliseconds)
		{
			//Logger.Debug("WaitForExit - time: {0}ms", milliseconds);

			// Workaround for a bug in which some data may still be processed AFTER this method returns true, thus losing the data.
			// http://connect.microsoft.com/VisualStudio/feedback/details/272125/waitforexit-and-waitforexit-int32-provide-different-and-undocumented-implementations
			bool waitSucceeded = Process.WaitForExit(milliseconds);
			if (waitSucceeded)
			{
				Process.WaitForExit();
			}
			return waitSucceeded;
		}

		struct CleanupData
		{
			public Process process;
			public ProcessStartInfo startInfo;
			public StreamWriter input;
			public ManualResetEventSlim done;
		}

		private static void Cleanup(ProcessWrapper wrapper)
		{
			var done = new ManualResetEventSlim(false);
			ThreadPool.QueueUserWorkItem(s => {
				var data = (CleanupData)s;

				try
				{
					if (data.startInfo.RedirectStandardError)
						data.process.CancelErrorRead();

				}
				catch { }
				try
				{
					if (data.startInfo.RedirectStandardOutput)
						data.process.CancelOutputRead();
				}
				catch { }

				try
				{
					data.input?.Dispose();
					data.process.Dispose();
				}
				finally
				{
					// without this the caller always sat out the full 200ms timeout below
					data.done.Set();
				}

			}, new CleanupData { done = done, input = wrapper.Input, process = wrapper.Process, startInfo = wrapper.StartInfo });
			done.Wait(200);
		}

		private bool disposed;

		protected override void Dispose(bool disposing)
		{
			if (disposed) return;
			if (disposing)
			{
				if (StartInfo.RedirectStandardError)
					Process.ErrorDataReceived -= OnErrorDataReceived;
				if (StartInfo.RedirectStandardOutput)
					Process.OutputDataReceived -= OnOutputDataReceived;
				stopEvent.Dispose();
				Cleanup(this);
				disposed = true;
			}
		}

		protected ILogging Logger { get { return logger = logger ?? LogHelper.GetLogger(GetType()); } }
		public Process Process { get; }
}
}
