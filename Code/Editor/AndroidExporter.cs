using System.IO;
using Editor.Android;

namespace Editor;

/// <summary>
/// Compiles the project, gathers its content and packs everything with the runtime bundle into an APK.
/// </summary>
public static class AndroidExporter
{
	public static AndroidExportSettings LoadSettings( Project project )
	{
		var defaults = AndroidExportSettings.ForProject( project.Config.Org, project.Config.Ident, project.Config.Title );
		defaults.OutputPath = Path.Combine( project.GetRootPath(), "Exports", "android", $"{project.Config.Ident}.apk" );

		var saved = ProjectCookie.Get<AndroidExportSettings>( CookieKey, null );
		return saved ?? defaults;
	}

	public static void SaveSettings( AndroidExportSettings settings ) => ProjectCookie.Set( CookieKey, settings );

	const string CookieKey = "AndroidExport.Settings";

	public static string FindSdk( AndroidExportSettings settings )
	{
		if ( !string.IsNullOrWhiteSpace( settings.SdkPath ) )
			return settings.SdkPath;

		return AndroidSdk.FindRoot( System.Environment.GetEnvironmentVariable, System.Environment.GetFolderPath( System.Environment.SpecialFolder.UserProfile ) );
	}

	/// <summary>
	/// Build the APK. Throws with a readable message if anything is wrong, long before the slow steps.
	/// </summary>
	public static async Task Export( Project project, AndroidExportSettings settings, Action<string> log )
	{
		var errors = settings.Validate();
		if ( errors.Count > 0 )
			throw new Exception( string.Join( "\n", errors ) );

		var bundle = new RuntimeBundle( settings.RuntimeDirectory, settings.NativeLibraryName, settings.EnableVR );
		if ( !bundle.IsValid )
			throw new Exception( string.Join( "\n", bundle.Errors ) );

		foreach ( var warning in bundle.Warnings )
			log( $"Warning: {warning}" );

		var sdk = AndroidSdk.Resolve( FindSdk( settings ), settings.TargetSdk, OperatingSystem.IsWindows() );
		log( $"Android SDK {sdk.Root} (build-tools {sdk.BuildToolsVersion}, platform {sdk.PlatformLevel})" );

		var runner = new ProcessToolRunner();
		var keystore = await ResolveKeystore( settings, runner );

		var manifestXml = AndroidManifestBuilder.Build( settings );

		log( "Compiling project.." );
		var compiled = await EditorUtility.Projects.Compile( project, s => log( $"[Compiler] {s}" ) );
		if ( compiled is null )
			throw new Exception( "Failed to compile project" );

		var entries = new List<ApkEntry>();

		var controlSettings = project.Config.TryGetMeta( "ControlModes", out ControlModeSettings modes ) ? modes : new();
		entries.Add( new AndroidRuntimeManifest
		{
			Name = project.Package.Title,
			Ident = project.Package.Ident,
			PackageName = settings.PackageName,
			BuildDate = DateTime.Now,
			IsVRProject = settings.EnableVR || controlSettings.IsVROnly
		}.ToEntry() );

		entries.AddRange( bundle.Entries() );

		foreach ( var assembly in compiled )
		{
			log( $"Adding {assembly.Compiler.AssemblyName}.dll" );
			entries.Add( AndroidRuntimeManifest.GameEntry( $".bin/{assembly.Compiler.AssemblyName}.dll", null, assembly.AssemblyData ) );
		}

		log( "Gathering assets.." );
		var manifest = new ProjectPublisher.PackageManifest();
		await manifest.BuildFromAssets( project );

		foreach ( var file in manifest.Assets )
		{
			var entry = AndroidRuntimeManifest.GameEntry( file.Name, file.AbsolutePath, file.Contents );
			if ( entry is null )
			{
				log( $"Warning: {file.Name} has no content, skipped" );
				continue;
			}

			entries.Add( entry );
		}

		var workDir = Path.Combine( Path.GetTempPath(), "sbox-android-" + Guid.NewGuid().ToString( "N" ) );
		try
		{
			await new ApkPackager( sdk, runner, log ).Package( settings, manifestXml, entries, keystore, workDir );
		}
		finally
		{
			try { Directory.Delete( workDir, true ); } catch { }
		}
	}

	static async Task<string> ResolveKeystore( AndroidExportSettings settings, IToolRunner runner )
	{
		if ( !string.IsNullOrWhiteSpace( settings.KeystorePath ) )
		{
			if ( !File.Exists( settings.KeystorePath ) )
				throw new FileNotFoundException( $"Keystore '{settings.KeystorePath}' doesn't exist" );

			return settings.KeystorePath;
		}

		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		var javaHome = Environment.GetEnvironmentVariable( "JAVA_HOME" );
		var keytool = string.IsNullOrEmpty( javaHome ) ? "keytool" : Path.Combine( javaHome, "bin", "keytool" );

		return await DebugKeystore.EnsureExists( DebugKeystore.DefaultPath( home ), keytool, runner );
	}

	public static async Task Deploy( AndroidExportSettings settings, Action<string> log )
	{
		var sdk = AndroidSdk.Resolve( FindSdk( settings ), settings.TargetSdk, OperatingSystem.IsWindows() );
		var adb = new AdbClient( sdk.Adb, new ProcessToolRunner() );

		var ready = (await adb.Devices()).Where( d => d.IsReady ).ToList();
		if ( ready.Count == 0 )
			throw new Exception( "No device connected. Enable developer mode on the headset, plug it in and accept the USB debugging prompt." );

		var device = ready[0];
		if ( ready.Count > 1 )
			log( $"{ready.Count} devices attached, using {device.Serial}" );

		log( $"Installing on {device.Serial} {device.Description}" );
		await adb.Install( settings.OutputPath, device.Serial );
		await adb.Launch( settings.PackageName, device.Serial );
		log( "Launched" );
	}
}
