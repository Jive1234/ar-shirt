using System; using System.Linq; using UnityEngine; using ARTryOn.Core; using ARTryOn.Filtering; using ARTryOn.Calibration; using ARTryOn.Tracking;
static class T {
  static int fails=0; static void Check(bool c,string m){Console.WriteLine((c?"PASS ":"FAIL ")+m); if(!c)fails++;}
  static Random rng=new Random(1); static float N(float s)=>(float)((rng.NextDouble()*2-1)*s);
  // A-pose person, facing camera, centre cx in image.
  static PersonObservation Person(float cx,float noise,float armAngleDeg=25,float shoulderW=0.40f){
    var p=new PersonObservation(); for(int i=0;i<Lm.Count;i++){p.Visibility[i]=0.95f;}
    float hs=shoulderW/2, a=armAngleDeg*Mathf.Deg2Rad; float ua=0.30f, fa=0.26f;
    var W=p.World;
    W[Lm.LeftHip]=new Vector3(0.15f,0,0); W[Lm.RightHip]=new Vector3(-0.15f,0,0);
    W[Lm.LeftShoulder]=new Vector3(hs,0.50f,0); W[Lm.RightShoulder]=new Vector3(-hs,0.50f,0);
    W[Lm.LeftElbow]=W[Lm.LeftShoulder]+new Vector3((float)Math.Sin(a)*ua,-(float)Math.Cos(a)*ua,0);
    W[Lm.RightElbow]=W[Lm.RightShoulder]+new Vector3(-(float)Math.Sin(a)*ua,-(float)Math.Cos(a)*ua,0);
    W[Lm.LeftWrist]=W[Lm.LeftElbow]+new Vector3((float)Math.Sin(a)*fa,-(float)Math.Cos(a)*fa,0);
    W[Lm.RightWrist]=W[Lm.RightElbow]+new Vector3(-(float)Math.Sin(a)*fa,-(float)Math.Cos(a)*fa,0);
    W[Lm.Nose]=new Vector3(0,0.75f,-0.05f);
    for(int i=0;i<Lm.Count;i++){ W[i]=W[i]+new Vector3(N(noise),N(noise),N(noise)); p.Image[i]=new Vector2(cx+W[i].x*0.5f+N(noise*0.5f),0.4f+W[i].y*0.5f+N(noise*0.5f)); }
    return p;
  }
  static int Main(){
    Check(Lm.MirrorOf.Length==33 && Enumerable.Range(0,33).All(i=>Lm.MirrorOf[Lm.MirrorOf[i]]==i), "MirrorOf is a 33-entry involution");
    Check(Lm.MirrorOf[Lm.LeftShoulder]==Lm.RightShoulder && Lm.MirrorOf[Lm.LeftHip]==Lm.RightHip, "MirrorOf pairs shoulders/hips");

    // One Euro: jitter reduction while still, low lag on a fast move.
    var f=new OneEuroFilter3(1.0f,1.0f); double t=0; float rawVar=0, outVar=0; int n=300;
    for(int i=0;i<n;i++){t+=1/30.0; var raw=new Vector3(0.5f+N(0.01f),0.5f+N(0.01f),0); var o=f.Filter(raw,t); if(i>30){rawVar+=(raw-new Vector3(.5f,.5f,0)).magnitude; outVar+=(o-new Vector3(.5f,.5f,0)).magnitude;}}
    Check(outVar<rawVar*0.4f, $"OneEuro still: jitter cut to {outVar/rawVar:P0} of raw");
    // step move 0.5 -> 0.8 at 2 units/s
    float x=0.5f; Vector3 o2=default; for(int i=0;i<30;i++){t+=1/30.0; x=Math.Min(0.8f,x+2f/30f); o2=f.Filter(new Vector3(x,0.5f,0),t);} 
    Check(Math.Abs(o2.x-0.8f)<0.02f, $"OneEuro fast move settles within 1s (err {Math.Abs(o2.x-0.8f):F4})");
    var f1=new OneEuroFilter(); f1.Filter(1,0); Check(f1.Filter(5,0)==1, "OneEuro ignores duplicate timestamp");

    // Calibration: hold 1.5s at 30fps with noise -> measurements near truth.
    var cs=new CalibrationSettings(); var sm=new CalibrationStateMachine(cs); var sm2=new PoseSmoother(OneEuroParams.DefaultImage,OneEuroParams.DefaultWorld);
    BodyMeasurements? got=null; sm.Calibrated+=m=>got=m; t=0; int frames=0;
    while(got==null && frames<300){t+=1/30.0; frames++; var raw=Person(0.5f,0.004f); sm.Tick(raw,sm2.Update(raw,t),t);}
    Check(got!=null, $"calibrated after {frames} frames ({frames/30.0:F2}s)");
    if(got!=null){var m=got.Value; Console.WriteLine("   "+m);
      Check(Math.Abs(m.ShoulderWidth-0.40f)<0.02f && Math.Abs(m.TorsoLength-0.50f)<0.02f && Math.Abs(m.UpperArmLength-0.30f)<0.02f && Math.Abs(m.ForearmLength-0.26f)<0.02f && Math.Abs(m.HipWidth-0.30f)<0.02f, "measurements within 2 cm");}
    Check(frames>=45 && frames<=55, "hold time ~1.5s");

    // Calibration rejects arms raised (T-pose) and restarts on movement.
    var smT=new CalibrationStateMachine(cs); t=0; for(int i=0;i<90;i++){t+=1/30.0; var r=Person(0.5f,0.002f,90); smT.Tick(r,r,t);} 
    Check(smT.State==CalibrationState.WaitingForPose && smT.Issue==PoseIssue.ArmsNotRelaxed, $"T-pose rejected ({smT.Issue})");
    var smM=new CalibrationStateMachine(cs); t=0; for(int i=0;i<90;i++){t+=1/30.0; var r=Person(0.3f+0.3f*(i%30)/30f,0.002f); smM.Tick(r,r,t);} 
    Check(smM.State!=CalibrationState.Calibrated && smM.Issue==PoseIssue.Moving, $"walking person not calibrated ({smM.Issue})");
    // brief wobble inside grace keeps progress
    var smG=new CalibrationStateMachine(cs); t=0; float progBefore=0; 
    for(int i=0;i<30;i++){t+=1/30.0; var r=Person(0.5f,0.002f); smG.Tick(r,r,t);} progBefore=smG.Progress;
    t+=1/30.0; var bad=Person(0.5f,0.002f,90); smG.Tick(bad,bad,t);
    for(int i=0;i<2;i++){t+=1/30.0; var r=Person(0.5f,0.002f); smG.Tick(r,r,t);} 
    Check(smG.State==CalibrationState.Collecting && smG.Progress>progBefore, "1-frame wobble within grace does not restart");

    // Tracker: two people, detection order swapped every frame -> stable ids, both calibrate.
    var tr=new PersonTracker(); int created=0, calibrated=0; tr.TrackCreated+=k=>{created++; k.Calibration.Calibrated+=_=>calibrated++;};
    t=0; for(int i=0;i<90;i++){t+=1/30.0; var fr=new PoseFrame{Timestamp=t}; var a=Person(0.25f,0.003f); var b=Person(0.75f,0.003f,25,0.36f); if(i%2==0){fr.People.Add(a);fr.People.Add(b);}else{fr.People.Add(b);fr.People.Add(a);} tr.Update(fr);} 
    Check(created==2 && tr.Tracks.Count==2, $"2 stable tracks (created {created})");
    Check(calibrated==2, $"both people calibrated ({calibrated})");
    var left=tr.Tracks.First(k=>k.Pose.TorsoCenter2D.x<0.5f); var right=tr.Tracks.First(k=>k.Pose.TorsoCenter2D.x>0.5f);
    Check(Math.Abs(right.Calibration.Result.ShoulderWidth-0.36f)<0.02f && Math.Abs(left.Calibration.Result.ShoulderWidth-0.40f)<0.02f, "per-person measurements not mixed up");
    // person leaves -> track dropped after timeout
    for(int i=0;i<40;i++){t+=1/30.0; var fr=new PoseFrame{Timestamp=t}; fr.People.Add(Person(0.25f,0.003f)); tr.Update(fr);} 
    Check(tr.Tracks.Count==1, "track removed after LostTimeout");
    Console.WriteLine(fails==0?"ALL PASS":$"{fails} FAILED"); return fails;
  }
}
