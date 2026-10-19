package com.megapdf.android;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.List;

/**
 * A provider that has the {@code _display_name} column but no usable value in it (#670),
 * for {@link DisplayNameTest}.
 *
 * It runs in the test APK's own process, under its own uid, which has no Kotlin runtime and
 * shares no memory with the test: so it is Java, and it takes everything from the uri it is
 * asked about. {@code /null/<name>} answers the display name as null, {@code /blank/<name>}
 * as whitespace; either way {@code <name>} is the uri's last segment. It serves
 * {@code hybrid-xfa.pdf} from this APK's assets, and keeps whatever is written to it (Save a
 * copy) in its own cache directory.
 */
public class NamelessProvider extends ContentProvider {
    @Override
    public boolean onCreate() {
        return true;
    }

    private File fileFor(Uri uri) {
        List<String> segments = uri.getPathSegments();
        return new File(getContext().getCacheDir(), segments.get(0) + "-" + segments.get(1));
    }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        String name = "blank".equals(uri.getPathSegments().get(0)) ? "  " : null;
        MatrixCursor cursor = new MatrixCursor(new String[] {OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE});
        cursor.addRow(new Object[] {name, null});
        return cursor;
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws java.io.FileNotFoundException {
        File file = fileFor(uri);
        try {
            if (!mode.contains("w") && file.length() == 0) {
                try (InputStream in = getContext().getAssets().open("hybrid-xfa.pdf");
                     FileOutputStream out = new FileOutputStream(file)) {
                    byte[] buffer = new byte[4096];
                    for (int n; (n = in.read(buffer)) > 0; ) out.write(buffer, 0, n);
                }
            }
        } catch (IOException e) {
            throw new java.io.FileNotFoundException(e.toString());
        }
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.parseMode(mode));
    }

    @Override
    public String getType(Uri uri) {
        return "application/pdf";
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        return null;
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        return 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return 0;
    }
}
