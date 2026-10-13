// SPDX-License-Identifier: GPL-3.0-or-later
import java.nio.file.*;
import javax.imageio.ImageIO;
import com.google.zxing.*;
import com.google.zxing.common.HybridBinarizer;
public final class QrImageRegression {
 public static void main(String[] args)throws Exception{
  var image=ImageIO.read(Path.of(args[0]).toFile());int w=image.getWidth(),h=image.getHeight();var pixels=image.getRGB(0,0,w,h,null,0,w);
  var result=new MultiFormatReader().decode(new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(w,h,pixels))));
  if(!result.getText().equals(args[1]))throw new IllegalStateException("QR round-trip mismatch");
  System.out.println("PASS: generated PNG decodes to the exact original QR content");
 }
}