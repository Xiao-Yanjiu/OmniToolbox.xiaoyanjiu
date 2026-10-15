// SPDX-License-Identifier: GPL-3.0-or-later
internal sealed class FrameClock {
 readonly double interval,tolerance;double next;bool started;
 public FrameClock(int fps,long frequency){interval=(double)frequency/fps;tolerance=frequency*.00075;}
 public bool Accept(long now){if(!started){started=true;next=now;}if(now+tolerance<next)return false;next+=interval;if(next<now)next=now+interval;return true;}
}
