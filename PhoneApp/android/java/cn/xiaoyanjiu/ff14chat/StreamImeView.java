package cn.xiaoyanjiu.ff14chat;
import android.view.*;import android.view.inputmethod.*;import android.text.*;
/** Invisible Android editor: composing text stays on the phone until committed by the IME. */
final class StreamImeView extends View {
 final MainActivity owner;final String session;boolean closed;
 StreamImeView(MainActivity owner,String session){super(owner);this.owner=owner;this.session=session;setFocusable(true);setFocusableInTouchMode(true);setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);}
 void close(){if(closed)return;closed=true;owner.finishIme(this);}
 @Override public boolean onCheckIsTextEditor(){return true;}
 @Override public boolean onKeyPreIme(int key,KeyEvent event){if(key==KeyEvent.KEYCODE_BACK&&event.getAction()==KeyEvent.ACTION_UP){close();return true;}return super.onKeyPreIme(key,event);}
 @Override public InputConnection onCreateInputConnection(EditorInfo info){info.inputType=android.text.InputType.TYPE_CLASS_TEXT;info.imeOptions=EditorInfo.IME_ACTION_DONE|EditorInfo.IME_FLAG_NO_EXTRACT_UI|EditorInfo.IME_FLAG_NO_FULLSCREEN;return new BaseInputConnection(this,true){
  @Override public boolean commitText(CharSequence text,int cursor){if(closed)return false;String value=text.toString();if(!value.isEmpty())owner.sendIme(session,value,0);super.commitText("",1);getEditable().clear();return true;}
  @Override public boolean deleteSurroundingText(int before,int after){Editable e=getEditable();if(e.length()>0)return super.deleteSurroundingText(before,after);if(before>0&&!closed)owner.sendIme(session,"",Math.min(before,100));return true;}
  @Override public boolean deleteSurroundingTextInCodePoints(int before,int after){return deleteSurroundingText(before,after);}
  @Override public boolean sendKeyEvent(KeyEvent event){if(closed)return false;if(event.getAction()!=KeyEvent.ACTION_DOWN)return true;if(event.getKeyCode()==KeyEvent.KEYCODE_DEL)return deleteSurroundingText(1,0);if(event.getKeyCode()==KeyEvent.KEYCODE_ENTER){close();return true;}int cp=event.getUnicodeChar();if(cp>0&&!Character.isISOControl(cp))owner.sendIme(session,new String(Character.toChars(cp)),0);return true;}
  @Override public boolean performEditorAction(int action){close();return true;}
 };}
}
