using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Editor.Android;

/// <summary>
/// The pieces of an installed Android SDK the exporter shells out to.
/// </summary>
public sealed class AndroidSdk
{
	public string Root { get; private init; }
	public string BuildToolsVersion { get; private init; }
	public string Aapt2 { get; private init; }
	public string ZipAlign { get; private init; }
	public string ApkSigner { get; private init; }
	public string Adb { get; private init; }
	public string AndroidJar { get; private init; }
	public int PlatformLevel { get; private init; }

	/// <summary>
	/// Where an SDK usually is: the two variables Google documents, then the default install folders.
	/// </summary>
	public static IEnumerable<string> CandidateRoots( Func<string, string> getEnv, string home )
	{
		var seen = new HashSet<string>();
		foreach ( var path in new[]
		{
			getEnv( "ANDROID_HOME" ),
			getEnv( "ANDROID_SDK_ROOT" ),
			Combine( getEnv( "LOCALAPPDATA" ), "Android", "Sdk" ),
			Combine( home, "Android", "Sdk" ),
			Combine( home, "Library", "Android", "sdk" ),
		} )
		{
			if ( !string.IsNullOrWhiteSpace( path ) && seen.Add( path ) )
				yield return path;
		}
	}

	static string Combine( string a, params string[] rest ) => string.IsNullOrWhiteSpace( a ) ? null : Path.Combine( new[] { a }.Concat( rest ).ToArray() );

	/// <summary>
	/// The first candidate root that really is an SDK, or null.
	/// </summary>
	public static string FindRoot( Func<string, string> getEnv, string home )
	{
		return CandidateRoots( getEnv, home ).FirstOrDefault( r => Directory.Exists( Path.Combine( r, "build-tools" ) ) );
	}

	/// <summary>
	/// Resolve tools from the SDK at <paramref name="root"/>. Picks the newest build-tools, and the
	/// lowest platform that is at least <paramref name="targetSdk"/> (falling back to the newest).
	/// Throws <see cref="FileNotFoundException"/> naming whatever is missing.
	/// </summary>
	public static AndroidSdk Resolve( string root, int targetSdk, bool windows = false )
	{
		if ( string.IsNullOrWhiteSpace( root ) || !Directory.Exists( root ) )
			throw new FileNotFoundException( "Android SDK not found. Set ANDROID_HOME or pick the SDK folder." );

		var buildToolsDir = Path.Combine( root, "build-tools" );
		var buildTools = ListVersions( buildToolsDir, name => Version.TryParse( name, out var v ) ? v : null );
		if ( buildTools.Count == 0 )
			throw new FileNotFoundException( $"No build-tools in {buildToolsDir}. Install them with the SDK manager." );

		var best = buildTools[^1];
		var toolsDir = Path.Combine( buildToolsDir, best.Name );
		var exe = windows ? ".exe" : "";

		var aapt2 = RequireFile( Path.Combine( toolsDir, "aapt2" + exe ) );
		var zipalign = RequireFile( Path.Combine( toolsDir, "zipalign" + exe ) );
		var apksigner = RequireFile( Path.Combine( toolsDir, windows ? "apksigner.bat" : "apksigner" ) );

		var platformsDir = Path.Combine( root, "platforms" );
		var platforms = ListVersions( platformsDir, name => name.StartsWith( "android-" ) && int.TryParse( name["android-".Length..], out var level ) ? new Version( level, 0 ) : null );
		if ( platforms.Count == 0 )
			throw new FileNotFoundException( $"No platforms in {platformsDir}. Install one with the SDK manager." );

		var platform = platforms.FirstOrDefault( p => p.Version.Major >= targetSdk ) ?? platforms[^1];
		var jar = RequireFile( Path.Combine( platformsDir, platform.Name, "android.jar" ) );

		var adb = Path.Combine( root, "platform-tools", "adb" + exe );

		return new AndroidSdk
		{
			Root = root,
			BuildToolsVersion = best.Name,
			Aapt2 = aapt2,
			ZipAlign = zipalign,
			ApkSigner = apksigner,
			AndroidJar = jar,
			PlatformLevel = platform.Version.Major,
			// adb is optional, only deploying needs it
			Adb = File.Exists( adb ) ? adb : null,
		};
	}

	record Entry( string Name, Version Version );

	static List<Entry> ListVersions( string dir, Func<string, Version> parse )
	{
		if ( !Directory.Exists( dir ) )
			return new();

		return Directory.GetDirectories( dir )
			.Select( d => Path.GetFileName( d ) )
			.Select( n => new Entry( n, parse( n ) ) )
			.Where( e => e.Version is not null )
			.OrderBy( e => e.Version )
			.ToList();
	}

	static string RequireFile( string path )
	{
		if ( !File.Exists( path ) )
			throw new FileNotFoundException( $"Missing Android SDK tool: {path}" );

		return path;
	}
}
