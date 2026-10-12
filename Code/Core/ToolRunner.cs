using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace Editor.Android;

public record ToolResult( int ExitCode, string Output )
{
	public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs an external command. Behind an interface so packaging can be tested without an SDK.
/// </summary>
public interface IToolRunner
{
	Task<ToolResult> Run( string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment = null );
}

public sealed class ProcessToolRunner : IToolRunner
{
	public async Task<ToolResult> Run( string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment = null )
	{
		// .bat/.cmd wrappers (apksigner on Windows) can't be started directly
		var windowsScript = executable.EndsWith( ".bat", System.StringComparison.OrdinalIgnoreCase ) || executable.EndsWith( ".cmd", System.StringComparison.OrdinalIgnoreCase );

		var info = new ProcessStartInfo
		{
			FileName = windowsScript ? "cmd.exe" : executable,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		if ( windowsScript )
		{
			info.ArgumentList.Add( "/c" );
			info.ArgumentList.Add( executable );
		}

		foreach ( var argument in arguments )
			info.ArgumentList.Add( argument );

		if ( environment is not null )
		{
			foreach ( var pair in environment )
				info.Environment[pair.Key] = pair.Value;
		}

		using var process = new Process { StartInfo = info };
		var output = new StringBuilder();
		void Append( string line ) { if ( line is not null ) lock ( output ) output.AppendLine( line ); }
		process.OutputDataReceived += ( _, e ) => Append( e.Data );
		process.ErrorDataReceived += ( _, e ) => Append( e.Data );

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		await process.WaitForExitAsync();

		return new ToolResult( process.ExitCode, output.ToString() );
	}
}
