package cn.xiaoyanjiu.ff14chat;

import android.content.*;
import android.database.*;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.*;

/** Exposes only the verified update APK, read only, under a temporary URI grant. */
public final class UpdateProvider extends ContentProvider {
 public boolean onCreate(){return true;}
 private File file(Uri uri)throws FileNotFoundException {
  if(!uri.toString().equals("content://"+getContext().getPackageName()+".updates/update.apk"))throw new FileNotFoundException("Invalid update URI");
  File f=new File(getContext().getCacheDir(),"update.apk");if(!f.isFile())throw new FileNotFoundException("Update not downloaded");return f;
 }
 public String getType(Uri uri){return "application/vnd.android.package-archive";}
 public ParcelFileDescriptor openFile(Uri uri,String mode)throws FileNotFoundException {if(!"r".equals(mode))throw new FileNotFoundException("Read only");return ParcelFileDescriptor.open(file(uri),ParcelFileDescriptor.MODE_READ_ONLY);}
 public Cursor query(Uri uri,String[] projection,String selection,String[] args,String order){try{File f=file(uri);String[] columns=projection==null?new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE}:projection;MatrixCursor cursor=new MatrixCursor(columns);Object[] values=new Object[columns.length];for(int i=0;i<columns.length;i++)values[i]=OpenableColumns.DISPLAY_NAME.equals(columns[i])?"小烟酒.apk":OpenableColumns.SIZE.equals(columns[i])?f.length():null;cursor.addRow(values);return cursor;}catch(FileNotFoundException e){return null;}}
 public Uri insert(Uri u,ContentValues v){throw new UnsupportedOperationException();}
 public int update(Uri u,ContentValues v,String s,String[] a){throw new UnsupportedOperationException();}
 public int delete(Uri u,String s,String[] a){throw new UnsupportedOperationException();}
}
