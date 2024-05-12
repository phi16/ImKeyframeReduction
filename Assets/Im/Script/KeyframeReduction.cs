//
// This codes is licensed under CC0 1.0.
// https://creativecommons.org/publicdomain/zero/1.0/
//

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using System;

#if UNITY_EDITOR
using UnityEditor;
using MathNet.Numerics;

namespace Im {

class ReductionCurve {
    public enum CurveType {
        Discrete,
        Linear,
        Smooth,
        Degree,
        Radian,
    }

    private CurveType curveType;
    public AnimationCurve curve;
    private int order;
    private double threshold;

    private bool first;
    private float lastValue;
    private double queueHeadTime;
    private double queueHeadValue;
    private Stack<double> queueTimes;
    private Stack<double> queueValues;
    private bool hasLastKeyframe;
    private Keyframe lastKeyframe;
    public ReductionCurve(double threshold, CurveType curveType = CurveType.Smooth) {
        this.threshold = threshold;
        this.curveType = curveType;
        if(curveType == CurveType.Discrete) {
            order = 0;
        } else if(curveType == CurveType.Linear) {
            order = 1;
        } else {
            order = 3;
        }

        curve = new AnimationCurve();
        first = true;
        queueHeadTime = 0;
        queueHeadValue = 0;
        queueTimes = new Stack<double>();
        queueValues = new Stack<double>();
        hasLastKeyframe = false;
        lastKeyframe = new Keyframe(0, 0);
    }

    public List<float> timeQueue = new List<float>();

    private float PickValue(float c) {
        if(first) {
            first = false;
            lastValue = c;
        }
        if(curveType == CurveType.Degree) {
            c -= lastValue;
            c = (((c + 180) % 360 + 360) % 360) - 180;
            c += lastValue;
            lastValue = c;
        }
        return c;
    }

    public void Tick(float t, float value) {
        float v = PickValue(value);
        if(queueTimes.Count == 0) {
            queueHeadTime = t;
            queueHeadValue = v;
        }
        queueTimes.Push(t - queueHeadTime);
        queueValues.Push(v - queueHeadValue);
        if(queueTimes.Count <= order + 1) {
            return;
        }

        double[] ts = queueTimes.ToArray();
        double[] vs = queueValues.ToArray();
        double[] coeffs = Fit.Polynomial(ts, vs, order);
        double res = 0;
        for(int i=0;i<queueTimes.Count;i++) {
            double ev = Polynomial.Evaluate(ts[i], coeffs);
            double d = vs[i] - ev;
            res += d * d;
        }
        if(res > threshold) {
            Flush(false);
        } 
    }

    public void Done() {
        Flush(true);
    }

    private Keyframe CreateKeyframe(double t, double[] coeffs) {
        double t0 = queueHeadTime;
        double t1 = queueHeadTime + t;
        double p0 = queueHeadValue + coeffs[0];
        double p1 = p0;
        for(int j=1;j<=order;j++) {
            p1 += coeffs[j] * Math.Pow(t, j);
        }
        double v0 = 0;
        double v1 = 0;
        if(order == 1) {
            v0 = v1 = coeffs[1];
        } else if(order == 3) {
            v0 = coeffs[1];
            v1 = coeffs[1] + 2 * coeffs[2] * t + 3 * coeffs[3] * t * t;
        }

        if(!hasLastKeyframe) {
            hasLastKeyframe = true;
            lastKeyframe = new Keyframe((float) t0, (float) p0, 0, 0);
        }

        lastKeyframe.outTangent = (float) v0;
        Keyframe keyframe = new Keyframe((float) t1, (float) p1, (float) v1, 0);
        return keyframe;
    }

    private void Flush(bool final) {
        if(queueTimes.Count == 0) return;

        double qht = 0;
        double qhv = 0;
        Stack<double> qt = new Stack<double>();
        Stack<double> qv = new Stack<double>();
        if(!final) {
            if(order == 3) {
                double t1 = queueTimes.Pop();
                double t0 = queueTimes.Pop();
                qht = queueHeadTime + t0;
                qt.Push(0);
                qt.Push(t1 - t0);
                double v1 = queueValues.Pop();
                double v0 = queueValues.Pop();
                qhv = queueHeadValue + v0;
                qv.Push(0);
                qv.Push(v1 - v0);
                queueTimes.Push(t0);
                queueValues.Push(v0);
            } else {
                double t0 = queueTimes.Pop();
                qht = queueHeadTime + t0;
                qt.Push(0);
                double v0 = queueValues.Pop();
                qhv = queueHeadValue + v0;
                qv.Push(0);
            }
        }
        double[] ts = queueTimes.ToArray();
        double[] vs = queueValues.ToArray();
        double[] coeffs;
        if(ts.Length >= order + 1) {
            coeffs = Fit.Polynomial(ts, vs, order);
        } else {
            double[] cs = Fit.Polynomial(ts, vs, ts.Length - 1);
            coeffs = new double[order+1];
            cs.CopyTo(coeffs, 0);
        }
        Keyframe keyframe = CreateKeyframe(ts[0], coeffs);
        int res = curve.AddKey(lastKeyframe);
        if(res == -1) {
            Debug.LogError("Failed to add keyframe");
        }
        if(final || order != 3) {
            curve.AddKey(keyframe);
            hasLastKeyframe = false;
        } else {
            lastKeyframe = keyframe;
        }

        queueHeadTime = qht;
        queueHeadValue = qhv;
        queueTimes = qt;
        queueValues = qv;
    }
}

public class KeyframeReduction : EditorWindow
{
    private AnimationClip clip;
    private AnimationClip outputClip;
    private double threshold = 0.000001;
    private float dt = 1 / 60.0f;
    private bool bruteForce = true;
    private bool cancel = false;

    [MenuItem("Window/Im/Keyframe Reduction")]
    public static void ShowWindow() {
        GetWindow(typeof(KeyframeReduction));
    }

    private void OnGUI() {
        GUILayout.Label("Keyframe Reduction", EditorStyles.boldLabel);
        clip = EditorGUILayout.ObjectField("Animation Clip", clip, typeof(AnimationClip), false) as AnimationClip;
        threshold = EditorGUILayout.DoubleField(new GUIContent("Threshold", "error tolerance threshold"), threshold);
        dt = EditorGUILayout.FloatField(new GUIContent("Delta Time [s]", "time interval for 1 frame"), dt);
        bruteForce = EditorGUILayout.Toggle(new GUIContent("Brute Force Mode", "If enabled, ALL FRAMES will be evaluated. High quality but long processing time."), bruteForce);
        EditorGUILayout.Space(10);
        outputClip = EditorGUILayout.ObjectField("Output Clip", outputClip, typeof(AnimationClip), false) as AnimationClip;
        if(GUILayout.Button("Execute")) {
            Run();
        }
    }

    void Run() {
        cancel = false;
        if(clip == null) return;
        AnimationClip ac = new AnimationClip();
        EditorCurveBinding[] allBindings = AnimationUtility.GetCurveBindings(clip);
        for(int i=0;i<allBindings.Length;i++) {
            EditorCurveBinding binding = allBindings[i];
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);

            ReductionCurve.CurveType type = ReductionCurve.CurveType.Smooth;
            if(binding.propertyName == "m_IsActive") type = ReductionCurve.CurveType.Discrete;
            if(binding.propertyName.Contains("Rotation")) type = ReductionCurve.CurveType.Radian;
            // Note: Add any binding type you want here, or send me a PR!

            AnimationCurve reduced = ExecuteReduction($"{binding.path}/{binding.propertyName} ({i+1}/{allBindings.Length})", curve, type);
            ac.SetCurve(binding.path, binding.type, binding.propertyName, reduced);

            if(cancel) break;
        }
        EditorUtility.ClearProgressBar(); 
        if(cancel) return;

        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        AnimationUtility.SetAnimationClipSettings(ac, settings);

        if(outputClip == null) {
            string path = AssetDatabase.GetAssetPath(clip);
            string newPath = $"{Path.GetDirectoryName(path)}/{Path.GetFileNameWithoutExtension(path)}_reduced.anim";
            Write(newPath, ac, true);
        } else {
            string path = AssetDatabase.GetAssetPath(outputClip);
            Write(path, ac, false);
        }
        outputClip = ac;
    }

    private AnimationCurve ExecuteReduction(string label, AnimationCurve c, ReductionCurve.CurveType t) {
        ReductionCurve k = new ReductionCurve(threshold, t);
        if(!bruteForce) {
            float lastTime = 0;
            for(int i=0;i<c.keys.Length;i++) {
                Keyframe key = c.keys[i];

                float tp2 = key.time - dt * 2;
                if(lastTime < tp2) k.Tick(tp2, c.Evaluate(tp2));

                float tp = key.time - dt;
                if(lastTime < tp) k.Tick(tp, c.Evaluate(tp));

                k.Tick(key.time, key.value);
                if(i != c.keys.Length-1) { 
                    float tf = key.time + dt;
                    k.Tick(tf, c.Evaluate(tf));
                    lastTime = tf;
                }
                if(EditorUtility.DisplayCancelableProgressBar("Keyframe Reduction", label, (float) i / c.keys.Length)) {
                    cancel = true;
                    break;
                }
            }
        } else {
            float endTime = c.keys[c.keys.Length-1].time;
            float lt = 0.0f;
            while(lt < endTime) {
                k.Tick(lt, c.Evaluate(lt)); 
                lt += dt;
                if(EditorUtility.DisplayCancelableProgressBar("Keyframe Reduction", label, lt / endTime)) {
                    cancel = true;
                    break;
                }
            }
            float eps = 0.0001f;
            if(lt + eps < endTime) k.Tick(endTime, c.Evaluate(endTime));
        }
        k.Done();
        return k.curve;
    }

    private void Write(string rawPath, AnimationClip clip, bool unique) {
        string path = unique
            ? AssetDatabase.GenerateUniqueAssetPath(rawPath)
            : rawPath;
        AssetDatabase.CreateAsset(clip, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }
}

}
#endif