package cn.xiaoyanjiu.ff14chat;

import android.app.Activity;
import android.content.Intent;
import android.hardware.Camera;
import android.os.Bundle;
import android.view.*;
import android.widget.*;
import com.google.zxing.*;
import com.google.zxing.common.HybridBinarizer;
import java.util.*;

@SuppressWarnings("deprecation")
public class ScanActivity extends Activity implements SurfaceHolder.Callback,Camera.PreviewCallback {
 private Camera camera;private boolean done;private long last;
 private final MultiFormatReader reader=new MultiFormatReader();
 @Override public void onCreate(Bundle b){super.onCreate(b);getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
  EnumMap<DecodeHintType,Object> hints=new EnumMap<>(DecodeHintType.class);hints.put(DecodeHintType.POSSIBLE_FORMATS,Collections.singletonList(BarcodeFormat.QR_CODE));hints.put(DecodeHintType.TRY_HARDER,true);reader.setHints(hints);
  FrameLayout layout=new FrameLayout(this);SurfaceView preview=new SurfaceView(this);layout.addView(preview);TextView caption=new TextView(this);caption.setText("对准电脑模块的配对二维码\n只扫描 FF14聊天 配对码");caption.setTextColor(-1);caption.setTextSize(18);caption.setPadding(24,50,24,20);layout.addView(caption);setContentView(layout);preview.getHolder().addCallback(this);
 }
 public void surfaceCreated(SurfaceHolder holder){try{camera=Camera.open();Camera.Parameters p=camera.getParameters();List<String> focus=p.getSupportedFocusModes();if(focus!=null&&focus.contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE))p.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);camera.setParameters(p);camera.setDisplayOrientation(90);camera.setPreviewDisplay(holder);camera.setPreviewCallback(this);camera.startPreview();}catch(Exception e){Toast.makeText(this,"无法打开相机，请改用手动配对",Toast.LENGTH_LONG).show();finish();}}
 public void surfaceChanged(SurfaceHolder h,int f,int w,int height){}
 public void surfaceDestroyed(SurfaceHolder h){release();}
 private void release(){if(camera!=null){camera.setPreviewCallback(null);camera.stopPreview();camera.release();camera=null;}}
 @Override public void onPause(){release();super.onPause();}
 public void onPreviewFrame(byte[] data,Camera cam){if(done||System.currentTimeMillis()-last<250)return;last=System.currentTimeMillis();
  try{Camera.Size s=cam.getParameters().getPreviewSize();LuminanceSource source=new PlanarYUVLuminanceSource(data,s.width,s.height,0,0,s.width,s.height,false);
   Result result=reader.decodeWithState(new BinaryBitmap(new HybridBinarizer(source)));if(result.getText().startsWith("ff14chat:")){done=true;setResult(RESULT_OK,new Intent().putExtra("qr",result.getText()));finish();}
  }catch(Exception ignored){}finally{reader.reset();}
 }
}
