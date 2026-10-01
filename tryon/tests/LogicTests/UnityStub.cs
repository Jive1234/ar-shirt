using System;
namespace UnityEngine {
public class Texture {}
public class TooltipAttribute : Attribute { public TooltipAttribute(string s){} }
public static class Mathf {
  public const float PI=(float)Math.PI, Deg2Rad=PI/180f, Rad2Deg=180f/PI;
  public static float Abs(float v)=>Math.Abs(v); public static float Min(float a,float b)=>Math.Min(a,b);
  public static float Clamp01(float v)=>v<0?0:v>1?1:v; public static float Clamp(float v,float a,float b)=>v<a?a:v>b?b:v;
  public static float Lerp(float a,float b,float t)=>a+(b-a)*Clamp01(t); public static float Sqrt(float v)=>(float)Math.Sqrt(v);
  public static float Acos(float v)=>(float)Math.Acos(v);
}
public struct Vector2 { public float x,y; public Vector2(float x,float y){this.x=x;this.y=y;}
  public static Vector2 operator+(Vector2 a,Vector2 b)=>new Vector2(a.x+b.x,a.y+b.y);
  public static Vector2 operator-(Vector2 a,Vector2 b)=>new Vector2(a.x-b.x,a.y-b.y);
  public static Vector2 operator*(Vector2 a,float s)=>new Vector2(a.x*s,a.y*s);
  public float magnitude=>Mathf.Sqrt(x*x+y*y);
  public static float Distance(Vector2 a,Vector2 b)=>(a-b).magnitude;
  public static implicit operator Vector3(Vector2 v)=>new Vector3(v.x,v.y,0);
}
public struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
  public static Vector3 zero=>new Vector3(0,0,0); public static Vector3 up=>new Vector3(0,1,0);
  public static Vector3 operator+(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
  public static Vector3 operator-(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
  public static Vector3 operator-(Vector3 a)=>new Vector3(-a.x,-a.y,-a.z);
  public static Vector3 operator*(Vector3 a,float s)=>new Vector3(a.x*s,a.y*s,a.z*s);
  public static Vector3 operator/(Vector3 a,float s)=>new Vector3(a.x/s,a.y/s,a.z/s);
  public float magnitude=>Mathf.Sqrt(x*x+y*y+z*z);
  public Vector3 normalized=>magnitude>1e-6f?this/magnitude:zero;
  public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;
  public static float Distance(Vector3 a,Vector3 b)=>(a-b).magnitude;
  public static float Angle(Vector3 a,Vector3 b){float d=Dot(a.normalized,b.normalized);return Mathf.Acos(Mathf.Clamp(d,-1,1))*Mathf.Rad2Deg;}
  public static Vector3 Lerp(Vector3 a,Vector3 b,float t)=>a+(b-a)*Mathf.Clamp01(t);
  public static implicit operator Vector2(Vector3 v)=>new Vector2(v.x,v.y);
}
}
