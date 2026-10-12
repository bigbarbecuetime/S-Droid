using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Editor.Android;

public record AdbDevice( string Serial, string State, string Description )
{
	public bool IsReady => State == "device";
}

/// <summary>
/// Install and launch over adb, which is how a Quest in developer mode takes builds.
/// </summary>
public class AdbClient
{
	readonly string adb;
	readonly IToolRunner runner;

	public AdbClient( string adb, IToolRunner runner )
	{
		this.adb = adb ?? throw new FileNotFoundException( "adb not found. Install 'platform-tools' with the Android SDK manager." );
		this.runner = runner;
	}

	/// <summary>Parse <c>adb devices -l</c>. Ignores the header, blank lines and daemon chatter.</summary>
	public static List<AdbDevice> ParseDevices( string output )
	{
		var devices = new List<AdbDevice>();

		foreach ( var raw in output.Split( '\n' ) )
		{
			var line = raw.Trim();
			if ( line.Length == 0 || line.StartsWith( "List of devices" ) || line.StartsWith( "*" ) )
				continue;

			var parts = line.Split( new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length < 2 )
				continue;

			devices.Add( new AdbDevice( parts[0], parts[1], parts.Length > 2 ? parts[2] : "" ) );
		}

		return devices;
	}

	public async Task<List<AdbDevice>> Devices()
	{
		var result = await runner.Run( adb, new[] { "devices", "-l" } );
		if ( !result.Success )
			throw new ApkBuildException( "adb devices failed: " + result.Output );

		return ParseDevices( result.Output );
	}

	/// <summary>Install (replacing any existing copy). Pass a serial when more than one device is attached.</summary>
	public async Task Install( string apk, string serial = null )
	{
		var result = await runner.Run( adb, With( serial, "install", "-r", apk ) );
		if ( !result.Success || !result.Output.Contains( "Success" ) )
			throw new ApkBuildException( "adb install failed: " + result.Output );
	}

	public async Task Launch( string packageName, string serial = null )
	{
		var result = await runner.Run( adb, With( serial, "shell", "am", "start", "-n", AndroidManifestBuilder.LaunchComponent( packageName ) ) );

		// am start exits 0 even when the component is missing
		if ( !result.Success || result.Output.Contains( "Error" ) )
			throw new ApkBuildException( "adb launch failed: " + result.Output );
	}

	static string[] With( string serial, params string[] args )
	{
		if ( string.IsNullOrEmpty( serial ) )
			return args;

		var all = new List<string> { "-s", serial };
		all.AddRange( args );
		return all.ToArray();
	}
}

/// <summary>
/// The debug keystore every Android toolchain shares, created with keytool if it doesn't exist yet.
/// </summary>
public static class DebugKeystore
{
	public const string Alias = "androiddebugkey";
	public const string Password = "android";

	public static string DefaultPath( string home ) => Path.Combine( home, ".android", "debug.keystore" );

	public static async Task<string> EnsureExists( string path, string keytool, IToolRunner runner )
	{
		if ( File.Exists( path ) )
			return path;

		Directory.CreateDirectory( Path.GetDirectoryName( path ) );

		var result = await runner.Run( keytool, new[]
		{
			"-genkeypair", "-keystore", path, "-storepass", Password, "-alias", Alias, "-keypass", Password,
			"-keyalg", "RSA", "-keysize", "2048", "-validity", "10000", "-dname", "CN=Android Debug,O=Android,C=US"
		} );

		if ( !result.Success )
			throw new ApkBuildException( "keytool failed:" + Environment.NewLine + result.Output );

		return path;
	}
}
