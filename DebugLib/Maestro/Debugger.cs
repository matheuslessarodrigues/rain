using System.IO;
using System.Net;
using System.Threading;

namespace Maestro.Debug
{
	public readonly struct SourcePosition
	{
		public readonly string sourceUri;
		public readonly int line;

		public SourcePosition(string sourceUri, int line)
		{
			this.sourceUri = sourceUri;
			this.line = line;
		}
	}

	public sealed partial class Debugger : IDebugger
	{
		private enum State
		{
			Paused,
			Continuing,
			Stepping,
		}

		private enum ConnectionState
		{
			Disconnected,
			WaitingDebugger,
			WaitingClient,
			Connected,
		}

		private readonly ProtocolServer server;
		private DebugSessionHelper helper = default;

		private Buffer<SourcePosition> breakpoints = new Buffer<SourcePosition>();

		private VirtualMachine vm;
		private Assembly assembly;
		private bool isConnected = false;
		private State state = State.Paused;
		private SourcePosition lastPosition = new SourcePosition();

		public Debugger()
		{
			server = new ProtocolServer(OnRequest);
		}

		public void Start(ushort port)
		{
			server.Start(IPAddress.Parse("127.0.0.1"), port);
		}

		public void Stop()
		{
			server.Stop();
		}

		public void WaitForClient()
		{
			while (true)
			{
				lock (this)
				{
					if (isConnected)
						break;
				}

				Thread.Sleep(1000);
			}
		}

		void IDebugger.OnBegin(VirtualMachine vm, Assembly assembly)
		{
			this.vm = vm;
			this.assembly = assembly;

			lock (this)
			{
				state = State.Continuing;
			}
		}

		void IDebugger.OnEnd(VirtualMachine vm, Assembly assembly)
		{
		}

		void IDebugger.OnHook(VirtualMachine vm, Assembly assembly)
		{
			var codeIndex = vm.stackFrames.buffer[vm.stackFrames.count - 1].codeIndex;
			if (codeIndex < 0)
				return;
			var sourceIndex = assembly.FindSourceIndex(codeIndex);
			if (sourceIndex < 0)
				return;

			var source = assembly.sources.buffer[sourceIndex];
			var slice = assembly.sourceSlices.buffer[codeIndex];
			var line = (ushort)(FormattingHelper.GetLineAndColumn(source.content, slice.index).lineIndex + 1);
			var position = new SourcePosition(source.uri, line);

			switch (state)
			{
			case State.Continuing:
				lock (this)
				{
					for (var i = 0; i < breakpoints.count; i++)
					{
						var breakpoint = breakpoints.buffer[i];
						var wasOnBreakpoint =
							// lastPosition.sourceUri == breakpoint.sourceUri &&
							lastPosition.line == breakpoint.line;

						if (!wasOnBreakpoint && position.line == breakpoint.line)
						{
							state = State.Paused;
							System.Console.WriteLine("SEND STOPPED FORM CONTINUING AT LINE {0}", position.line);
							SendStoppedEvent("breakpoint");
							break;
						}
					}
				}
				break;
			case State.Stepping:
				if (lastPosition.sourceUri != position.sourceUri || lastPosition.line != position.line)
				{
					lock (this)
					{
						state = State.Paused;
						System.Console.WriteLine("SEND STOPPED FORM STEPPING");
						SendStoppedEvent("step");
					}
					break;
				}
				break;
			}

			while (true)
			{
				lock (this)
				{
					if (state != State.Paused)
						break;
				}

				Thread.Sleep(1000);
			}

			lastPosition = position;
		}

		private bool TryMatchSourcePath(string sourceUri, out string sourcePath)
		{
			sourceUri = sourceUri.Replace(Path.AltDirectorySeparatorChar, Path.PathSeparator);
			for (var i = 0; i < breakpoints.count; i++)
			{
				sourcePath = breakpoints.buffer[i].sourceUri;
				sourcePath = sourcePath.Replace(Path.AltDirectorySeparatorChar, Path.PathSeparator);

				if (sourcePath.EndsWith(sourceUri))
					return true;
			}

			sourcePath = null;
			return false;
		}
	}
}