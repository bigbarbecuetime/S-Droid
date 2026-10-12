using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Editor.Android;

/// <summary>
/// The arm64 Android build of the s&amp;box runtime that gets packed into the APK. s&amp;box doesn't ship
/// one, so it's an input: a folder with <c>lib/arm64-v8a/lib&lt;name&gt;.so</c> (plus the libraries it
/// loads) and optionally <c>assets/</c> with engine content to merge into the APK's assets.
/// </summary>
public sealed class RuntimeBundle
{
	public const string LibDirectory = "lib/" + ApkPackager.Abi;

	/// <summary>Khronos OpenXR loader. Meta supports it from 1.0.34, older ones crash on Quest.</summary>
	public const string OpenXrLoader = "libopenxr_loader.so";

	public string Directory { get; }
	public List<string> Errors { get; } = new();
	public List<string> Warnings { get; } = new();

	public RuntimeBundle( string directory, string libraryName, bool vr )
	{
		Directory = directory;

		if ( string.IsNullOrWhiteSpace( directory ) || !System.IO.Directory.Exists( directory ) )
		{
			Errors.Add( $"Android runtime directory '{directory}' doesn't exist" );
			return;
		}

		var libDir = Path.Combine( directory, "lib", ApkPackager.Abi );
		if ( !System.IO.Directory.Exists( libDir ) )
		{
			Errors.Add( $"{LibDirectory} is missing from the runtime directory. Quest and modern Android devices are arm64 only" );
			return;
		}

		var libs = System.IO.Directory.GetFiles( libDir, "*.so" );
		if ( libs.Length == 0 )
		{
			Errors.Add( $"No .so files in {libDir}" );
			return;
		}

		var main = $"lib{libraryName}.so";
		if ( !libs.Any( l => Path.GetFileName( l ) == main ) )
			Errors.Add( $"{main} is missing from {libDir}, NativeActivity can't start without it" );

		if ( vr && !libs.Any( l => Path.GetFileName( l ) == OpenXrLoader ) )
			Warnings.Add( $"{OpenXrLoader} isn't in {libDir}. Fine if the runtime links the Khronos loader (1.0.34+) statically, otherwise it won't find OpenXR on the headset" );
	}

	public bool IsValid => Errors.Count == 0;

	/// <summary>
	/// Native libraries, then the bundle's assets (under <c>assets/</c>), as APK entries.
	/// </summary>
	public IEnumerable<ApkEntry> Entries()
	{
		if ( !IsValid )
			throw new InvalidOperationException( "Runtime bundle is invalid: " + string.Join( "; ", Errors ) );

		var libDir = Path.Combine( Directory, "lib", ApkPackager.Abi );
		foreach ( var lib in System.IO.Directory.GetFiles( libDir, "*.so" ).OrderBy( x => x, StringComparer.Ordinal ) )
			yield return new ApkEntry( $"{LibDirectory}/{Path.GetFileName( lib )}", SourcePath: lib, Compress: false );

		var assetsDir = Path.Combine( Directory, "assets" );
		if ( !System.IO.Directory.Exists( assetsDir ) )
			yield break;

		foreach ( var file in System.IO.Directory.GetFiles( assetsDir, "*", SearchOption.AllDirectories ).OrderBy( x => x, StringComparer.Ordinal ) )
			yield return new ApkEntry( "assets/" + Path.GetRelativePath( assetsDir, file ).Replace( '\\', '/' ), SourcePath: file );
	}
}

/// <summary>
/// Written to <c>assets/sbox/android.json</c> so the native runtime knows what game it was packed with.
/// The Android counterpart of <c>StandaloneManifest</c>, which Windows exports embed in the exe.
/// </summary>
public class AndroidRuntimeManifest
{
	public const string AssetPath = "assets/sbox/android.json";

	/// <summary>Where project content lives in the APK's assets.</summary>
	public const string GameRoot = "assets/sbox/game/";

	public string Name { get; set; }
	public string Ident { get; set; }
	public string PackageName { get; set; }
	public DateTime BuildDate { get; set; }
	public bool IsVRProject { get; set; }
	public string GameRootPath { get; set; } = "sbox/game/";

	/// <summary>
	/// An APK entry for one project file, under <see cref="GameRoot"/>. Null if the file has no content at all.
	/// </summary>
	public static ApkEntry GameEntry( string relativeName, string absolutePath, byte[] contents )
	{
		var name = GameRoot + relativeName.Replace( '\\', '/' ).TrimStart( '/' );

		if ( contents is not null )
			return new ApkEntry( name, Contents: contents );

		return absolutePath is null ? null : new ApkEntry( name, SourcePath: absolutePath );
	}

	public ApkEntry ToEntry() => new( AssetPath, Contents: JsonSerializer.SerializeToUtf8Bytes( this, new JsonSerializerOptions { WriteIndented = true } ) );
}
