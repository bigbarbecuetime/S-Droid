package game.sbox.runtime;

import android.app.Activity;
import android.os.Bundle;
import android.util.Log;
import android.widget.ScrollView;
import android.widget.TextView;

import java.io.BufferedReader;
import java.io.File;
import java.io.InputStreamReader;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;

/**
 * Starts the s&amp;box engine (x86-64 Linux build, with its own glibc) from this app's files
 * directory and shows its output. Arguments come from the "args" intent extra.
 */
public class RuntimeActivity extends Activity
{
	static final String TAG = "sbox";

	TextView output;
	ScrollView scroll;

	@Override
	protected void onCreate( Bundle state )
	{
		super.onCreate( state );
		output = new TextView( this );
		output.setTextIsSelectable( true );
		output.setTypeface( android.graphics.Typeface.MONOSPACE );
		output.setTextSize( 10 );
		scroll = new ScrollView( this );
		scroll.addView( output );
		setContentView( scroll );

		String args = getIntent().getStringExtra( "args" );
		if ( args == null ) args = "+game unittest/addons/spacewars/.sbproj";
		final List<String> argv = new ArrayList<>( Arrays.asList( args.trim().split( "\\s+" ) ) );

		// Seconds to wait before starting, to attach a tracer to this process
		final int delay = getIntent().getIntExtra( "delay", 0 );

		new Thread( () -> {
			if ( delay > 0 )
			{
				show( "waiting " + delay + "s, pid " + android.os.Process.myPid() );
				try { Thread.sleep( delay * 1000L ); } catch ( InterruptedException ignored ) { }
			}
			run( argv );
		}, "sbox-engine" ).start();
	}

	void show( String line )
	{
		Log.i( TAG, line );
		runOnUiThread( () -> {
			output.append( line + "\n" );
			scroll.post( () -> scroll.fullScroll( ScrollView.FOCUS_DOWN ) );
		} );
	}

	void run( List<String> args )
	{
		File root = new File( getFilesDir(), "sbox" );
		File game = new File( root, "game" );
		File server = new File( game, "sbox-server" );

		if ( !server.canExecute() )
		{
			show( "No engine payload at " + root + " - install it with: adb shell run-as " + getPackageName() + " ..." );
			return;
		}

		List<String> command = new ArrayList<>();
		command.add( server.getPath() );
		command.addAll( args );

		ProcessBuilder pb = new ProcessBuilder( command ).directory( game ).redirectErrorStream( true );
		Map<String, String> env = pb.environment();
		String lib = root + "/rootfs/lib/x86_64-linux-gnu";
		env.put( "LD_LIBRARY_PATH", lib );
		env.put( "HOME", new File( root, "home" ).getPath() );
		env.put( "TMPDIR", getCacheDir().getPath() );
		env.put( "LANG", "C.UTF-8" );
		new File( root, "home" ).mkdirs();

		show( "pid " + android.os.Process.myPid() + " uid " + android.os.Process.myUid() + " starting: " + command );
		try
		{
			Process p = pb.start();
			p.getOutputStream().close();
			try ( BufferedReader reader = new BufferedReader( new InputStreamReader( p.getInputStream() ) ) )
			{
				String line;
				while ( ( line = reader.readLine() ) != null )
					show( line );
			}
			show( "engine exited with " + p.waitFor() );
		}
		catch ( Exception e )
		{
			show( "failed: " + e );
		}
	}
}
