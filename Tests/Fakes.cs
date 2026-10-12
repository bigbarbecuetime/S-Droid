using Editor.Android;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace AndroidExportTests;

/// <summary>
/// Stands in for the SDK tools. Records every call and mimics the files each tool would produce, so
/// the packager's real zip handling runs against believable inputs.
/// </summary>
class FakeToolRunner : IToolRunner
{
	public record Call( string Tool, string[] Args, Dictionary<string, string> Env );

	public List<Call> Calls { get; } = new();
	public Dictionary<string, ToolResult> Failures { get; } = new();

	public Task<ToolResult> Run( string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment = null )
	{
		var args = arguments.ToArray();
		Calls.Add( new Call( executable, args, environment?.ToDictionary( x => x.Key, x => x.Value ) ) );

		var name = Path.GetFileName( executable );
		if ( Failures.TryGetValue( name, out var failure ) )
			return Task.FromResult( failure );

		switch ( name )
		{
			case "aapt2":
				WriteLinkedApk( ValueAfter( args, "-o" ) );
				break;
			case "zipalign":
				File.Copy( args[^2], args[^1], true );
				break;
			case "apksigner" when args[0] == "sign":
				File.Copy( args[^1], ValueAfter( args, "--out" ), true );
				break;
		}

		return Task.FromResult( new ToolResult( 0, "ok" ) );
	}

	public static string ValueAfter( string[] args, string flag ) => args[System.Array.IndexOf( args, flag ) + 1];

	// What aapt2 link gives: a manifest and a resource table
	static void WriteLinkedApk( string path )
	{
		using var zip = ZipFile.Open( path, ZipArchiveMode.Create );
		using ( var w = new StreamWriter( zip.CreateEntry( "AndroidManifest.xml" ).Open() ) ) w.Write( "binary manifest" );
		using ( var w = new StreamWriter( zip.CreateEntry( "resources.arsc", CompressionLevel.NoCompression ).Open() ) ) w.Write( "table" );
	}
}

static class ZipInspector
{
	/// <summary>Compression method per entry (0 stored, 8 deflate), read straight from the central directory.</summary>
	public static Dictionary<string, int> Methods( string path )
	{
		var bytes = File.ReadAllBytes( path );

		var eocd = -1;
		for ( var i = bytes.Length - 22; i >= 0; i-- )
		{
			if ( bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 5 && bytes[i + 3] == 6 ) { eocd = i; break; }
		}

		Assert.IsTrue( eocd >= 0, "no end of central directory" );

		var count = System.BitConverter.ToUInt16( bytes, eocd + 10 );
		var offset = (int)System.BitConverter.ToUInt32( bytes, eocd + 16 );
		var methods = new Dictionary<string, int>();

		for ( var n = 0; n < count; n++ )
		{
			var method = System.BitConverter.ToUInt16( bytes, offset + 10 );
			var nameLength = System.BitConverter.ToUInt16( bytes, offset + 28 );
			var extraLength = System.BitConverter.ToUInt16( bytes, offset + 30 );
			var commentLength = System.BitConverter.ToUInt16( bytes, offset + 32 );
			methods[System.Text.Encoding.UTF8.GetString( bytes, offset + 46, nameLength )] = method;
			offset += 46 + nameLength + extraLength + commentLength;
		}

		return methods;
	}
}

class TempDir : System.IDisposable
{
	public string Path { get; } = System.IO.Path.Combine( System.IO.Path.GetTempPath(), "sbox-android-test-" + System.Guid.NewGuid().ToString( "N" ) );

	public TempDir() => Directory.CreateDirectory( Path );

	public string File( string relative, string contents = "x" )
	{
		var full = System.IO.Path.Combine( Path, relative );
		Directory.CreateDirectory( System.IO.Path.GetDirectoryName( full ) );
		System.IO.File.WriteAllText( full, contents );
		return full;
	}

	public string Dir( string relative )
	{
		var full = System.IO.Path.Combine( Path, relative );
		Directory.CreateDirectory( full );
		return full;
	}

	public void Dispose() { try { Directory.Delete( Path, true ); } catch { } }
}
