using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Editor.Android;

/// <summary>
/// One file to put in the APK. Exactly one of <see cref="SourcePath"/> and <see cref="Contents"/> is set.
/// </summary>
public record ApkEntry( string Name, string SourcePath = null, byte[] Contents = null, bool Compress = true )
{
	/// <summary>Write this entry's bytes to <paramref name="target"/>.</summary>
	public void CopyTo( Stream target )
	{
		if ( Contents is not null )
		{
			target.Write( Contents );
			return;
		}

		using var source = File.OpenRead( SourcePath );
		source.CopyTo( target );
	}
}

public class ApkBuildException : Exception
{
	public ApkBuildException( string message ) : base( message ) { }
}

/// <summary>
/// Turns a manifest plus files into a signed APK with the SDK's own tools: aapt2 link for the binary
/// manifest, our own zip pass for native libs and assets, zipalign, then apksigner.
/// Because the activity is NativeActivity there is no dex step.
/// </summary>
public class ApkPackager
{
	readonly AndroidSdk sdk;
	readonly IToolRunner runner;
	readonly Action<string> log;

	public const string Abi = "arm64-v8a";
	const string PasswordVar = "SBOX_ANDROID_KS_PASS";
	const string KeyPasswordVar = "SBOX_ANDROID_KEY_PASS";

	public ApkPackager( AndroidSdk sdk, IToolRunner runner, Action<string> log = null )
	{
		this.sdk = sdk;
		this.runner = runner;
		this.log = log ?? (_ => { });
	}

	/// <summary>
	/// Build, align and sign the APK at <see cref="AndroidExportSettings.OutputPath"/>.
	/// </summary>
	public async Task Package( AndroidExportSettings settings, string manifestXml, IEnumerable<ApkEntry> entries, string keystorePath, string workDir )
	{
		Directory.CreateDirectory( workDir );

		var manifestPath = Path.Combine( workDir, "AndroidManifest.xml" );
		var linkedPath = Path.Combine( workDir, "linked.apk" );
		var unalignedPath = Path.Combine( workDir, "unaligned.apk" );
		var alignedPath = Path.Combine( workDir, "aligned.apk" );
		File.WriteAllText( manifestPath, manifestXml );

		log( "Linking manifest.." );
		await Run( sdk.Aapt2, "aapt2 link",
			"link", "-o", linkedPath, "-I", sdk.AndroidJar, "--manifest", manifestPath,
			"--min-sdk-version", settings.MinSdk.ToString(), "--target-sdk-version", settings.TargetSdk.ToString(),
			"--version-code", settings.VersionCode.ToString(), "--version-name", settings.VersionName );

		log( "Adding libraries and assets.." );
		AddEntries( linkedPath, unalignedPath, entries );

		log( "Aligning.." );
		// -p: page align stored .so files so they can be mapped directly, which extractNativeLibs=false needs
		await Run( sdk.ZipAlign, "zipalign", "-f", "-p", "4", unalignedPath, alignedPath );

		log( "Signing.." );
		var output = settings.OutputPath;
		Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( output ) ) );
		if ( File.Exists( output ) ) File.Delete( output );

		// Passwords go through the environment, not the command line, so they don't show in process lists
		await Run( sdk.ApkSigner, "apksigner",
			new Dictionary<string, string> { [PasswordVar] = settings.KeystorePassword, [KeyPasswordVar] = settings.KeyPassword },
			"sign", "--ks", keystorePath, "--ks-key-alias", settings.KeystoreAlias,
			"--ks-pass", $"env:{PasswordVar}", "--key-pass", $"env:{KeyPasswordVar}",
			"--out", output, alignedPath );

		await Run( sdk.ApkSigner, "apksigner verify", "verify", output );
		log( $"Wrote {output}" );
	}

	Task Run( string tool, string title, params string[] args ) => Run( tool, title, null, args );

	async Task Run( string tool, string title, IReadOnlyDictionary<string, string> environment, params string[] args )
	{
		var result = await runner.Run( tool, args, environment );
		if ( !result.Success )
			throw new ApkBuildException( $"{title} failed (exit code {result.ExitCode}):{Environment.NewLine}{result.Output}" );
	}

	/// <summary>
	/// Write <paramref name="destPath"/> as a copy of the zip at <paramref name="sourcePath"/> plus
	/// <paramref name="entries"/>. Streams to disk (appending in place would buffer every new entry in
	/// memory, and game content runs to gigabytes). Native libs are stored uncompressed so zipalign can
	/// page align them, and so is resources.arsc, which Android 11+ requires. Names are normalised to
	/// forward slashes, and duplicates are rejected rather than silently shadowed.
	/// </summary>
	public static void AddEntries( string sourcePath, string destPath, IEnumerable<ApkEntry> entries )
	{
		using var input = ZipFile.OpenRead( sourcePath );
		using var output = new FileStream( destPath, FileMode.Create, FileAccess.Write );
		using var zip = new ZipArchive( output, ZipArchiveMode.Create );

		var names = new HashSet<string>( StringComparer.Ordinal );

		foreach ( var existing in input.Entries )
		{
			names.Add( existing.FullName );

			var level = existing.FullName == "resources.arsc" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
			var copy = zip.CreateEntry( existing.FullName, level );

			using var from = existing.Open();
			using var to = copy.Open();
			from.CopyTo( to );
		}

		foreach ( var entry in entries )
		{
			var name = entry.Name.Replace( '\\', '/' ).TrimStart( '/' );
			if ( name.Length == 0 || name.Split( '/' ).Contains( ".." ) )
				throw new ApkBuildException( $"Invalid APK entry name '{entry.Name}'" );

			if ( !names.Add( name ) )
				throw new ApkBuildException( $"Duplicate APK entry '{name}'" );

			var compress = entry.Compress && !name.EndsWith( ".so", StringComparison.Ordinal );
			var zipEntry = zip.CreateEntry( name, compress ? CompressionLevel.Fastest : CompressionLevel.NoCompression );

			using var target = zipEntry.Open();
			entry.CopyTo( target );
		}
	}
}
