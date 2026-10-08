using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace DigiPhant
{
    // Capture the complete Game view, including camera preview, without taking ownership of the webcam.
    public class DigiPhantRecording : MonoBehaviour
    {
        public bool IsRecording { get; private set; }
        public string Status { get; private set; } = "Record camera and elephant together (no audio).";
        const int Fps = 10;
        string session, python, tracking;
        float started;
        int frames;
        StreamWriter frameLog;
        Coroutine capture;
        Process encoder;
        [Serializable] class Frame { public string file; public float time; }
        [Serializable] class Session { public int fps = Fps; public float duration; public int capturedFrames; public string content = "Unity Game view: camera and elephant; no audio"; }
        [Serializable] class EncodeRequest { public string session; }

        public void BeginRecording()
        {
            if (IsRecording || encoder != null) return;
#if UNITY_EDITOR
            var controller = GetComponent<DigiPhantController>();
            var preview = GetComponent<DigiPhantCameraPreview>();
            if (controller == null || controller.inputMode != InputMode.Camera || !controller.IsCalibrated ||
                preview == null || !preview.HasLiveFrame)
            { Status = "Select Camera, wait for live preview and calibrate before recording."; return; }
            controller.showControls = true;
            preview.showPreview = true;
            tracking = Path.GetFullPath(Path.Combine(Application.dataPath, "../DigiPhantStarter/Tracking"));
            python = Path.Combine(tracking, Application.platform == RuntimePlatform.WindowsEditor ? ".venv/Scripts/python.exe" : ".venv/bin/python");
            if (!File.Exists(python) || !File.Exists(Path.Combine(tracking, "encode_recording.py")))
            { Status = "Recording setup missing. Follow DigiPhantStarter/RECORDING.md."; return; }
            try
            {
                session = Path.GetFullPath(Path.Combine(Application.dataPath, "../Recordings",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
                Directory.CreateDirectory(session);
                frameLog = new StreamWriter(Path.Combine(session, "frames.jsonl"));
                frames = 0;
                started = Time.realtimeSinceStartup;
                IsRecording = true;
                Status = "Recording — keep Game view visible.";
                capture = StartCoroutine(CaptureFrames());
            }
            catch (Exception e) { frameLog?.Dispose(); frameLog = null; IsRecording = false; Status = "Recording could not start: " + e.Message; }
#else
            Status = "Built-in recording requires the Unity Editor.";
#endif
        }
        IEnumerator CaptureFrames()
        {
            var end = new WaitForEndOfFrame();
            while (IsRecording)
            {
                yield return end;
                if (!IsRecording) yield break;
                string error = CaptureFrame();
                if (error != null)
                {
                    FinishCapture(false);
                    Status = "Recording stopped: " + error + ". Saved frames: " + session;
                    yield break;
                }
                yield return new WaitForSecondsRealtime(1f / Fps);
            }
        }
        string CaptureFrame()
        {
            Texture2D screenshot = null, reduced = null;
            RenderTexture target = null;
            var previous = RenderTexture.active;
            try
            {
                float capturedAt = Time.realtimeSinceStartup - started;
                screenshot = ScreenCapture.CaptureScreenshotAsTexture();
                if (screenshot == null) return "Game view capture unavailable";
                float scale = Mathf.Min(1, 1280f / screenshot.width, 720f / screenshot.height);
                int w = Mathf.Max(2, Mathf.FloorToInt(screenshot.width * scale / 2) * 2);
                int h = Mathf.Max(2, Mathf.FloorToInt(screenshot.height * scale / 2) * 2);
                target = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(screenshot, target);
                RenderTexture.active = target;
                reduced = new Texture2D(w, h, TextureFormat.RGB24, false);
                reduced.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                reduced.Apply();
                string name = frames.ToString("D6") + ".jpg";
                File.WriteAllBytes(Path.Combine(session, name), ImageConversion.EncodeToJPG(reduced, 85));
                frameLog.WriteLine(JsonUtility.ToJson(new Frame { file = name, time = capturedAt }));
                frameLog.Flush();
                frames++;
                return null;
            }
            catch (Exception e) { return e.Message; }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (screenshot != null) Destroy(screenshot);
                if (reduced != null) Destroy(reduced);
            }
        }
        public void StopRecording()
        {
            if (!IsRecording) return;
            if (capture != null) StopCoroutine(capture);
            capture = null;
            FinishCapture(true);
        }
        void FinishCapture(bool encode)
        {
            IsRecording = false;
            frameLog?.Dispose(); frameLog = null;
            try
            {
                File.WriteAllText(Path.Combine(session, "session.json"), JsonUtility.ToJson(new Session {
                    duration = Mathf.Max(.001f, Time.realtimeSinceStartup - started), capturedFrames = frames }, true));
                if (frames == 0) { Status = "No frames captured. Keep Game view visible and try again."; return; }
                if (!encode) return;
                var info = new ProcessStartInfo(python, "-u encode_recording.py") {
                    WorkingDirectory = tracking, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true
                };
                encoder = Process.Start(info);
                encoder.StandardInput.WriteLine(JsonUtility.ToJson(new EncodeRequest { session = session }));
                encoder.StandardInput.Close();
                Status = "Saving MP4... " + session;
            }
            catch (Exception e) { encoder?.Dispose(); encoder = null; Status = "Save failed: " + e.Message + ". Frames retained at " + session; }
        }
        void Update()
        {
            if (encoder == null || !encoder.HasExited) return;
            Status = encoder.ExitCode == 0 && File.Exists(Path.Combine(session, "performance.mp4"))
                ? "Saved: " + Path.Combine(session, "performance.mp4")
                : "MP4 export failed. Frames retained: " + session + ". See RECORDING.md to retry.";
            encoder.Dispose(); encoder = null;
        }
        public void DrawControls(float width)
        {
            if (IsRecording)
            {
                GUILayout.Label("RECORDING · " + Mathf.FloorToInt(Time.realtimeSinceStartup - started) + " seconds");
                if (GUILayout.Button("Stop recording and save")) StopRecording();
            }
            else
            {
                bool wasEnabled = GUI.enabled;
                GUI.enabled = wasEnabled && encoder == null;
                if (GUILayout.Button("Record performance")) BeginRecording();
                GUI.enabled = wasEnabled;
            }
            GUILayout.Label(Status, new GUIStyle(GUI.skin.label) { wordWrap = true }, GUILayout.Width(width));
        }
        void OnDisable()
        {
            StopRecording();
            // Export can finish after Play stops; disposing the handle does not kill Python.
            encoder?.Dispose(); encoder = null;
        }
    }
}
