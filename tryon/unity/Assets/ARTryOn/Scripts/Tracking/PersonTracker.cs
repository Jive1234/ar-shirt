using System.Collections.Generic;
using ARTryOn.Calibration;
using ARTryOn.Core;
using ARTryOn.Filtering;
using UnityEngine;

namespace ARTryOn.Tracking
{
    /// <summary>One person with a stable id across frames. Owns its own filter and calibration.</summary>
    public sealed class Track
    {
        public readonly int Id;
        public readonly PoseSmoother Smoother;
        public readonly CalibrationStateMachine Calibration;
        public PersonObservation Raw;
        public PersonObservation Pose => Smoother.Smoothed;
        public double LastSeen;
        public bool SeenThisFrame;

        public Track(int id, OneEuroParams img, OneEuroParams world, CalibrationSettings calib)
        {
            Id = id;
            Smoother = new PoseSmoother(img, world);
            Calibration = new CalibrationStateMachine(calib);
        }
    }

    /// <summary>
    /// MediaPipe returns people in arbitrary order every frame. This assigns stable ids by greedy
    /// nearest-neighbour matching on the torso centre, which is enough for 2–4 people who do not
    /// swap places while overlapping. For crowded scenes, add an appearance embedding (e.g. colour
    /// histogram of the person mask) to the cost.
    /// </summary>
    public sealed class PersonTracker
    {
        public float MaxMatchDistance = 0.15f; // normalized image units per frame
        public float LostTimeout = 1.0f;       // seconds a track survives without detections

        public OneEuroParams ImageFilter = OneEuroParams.DefaultImage;
        public OneEuroParams WorldFilter = OneEuroParams.DefaultWorld;
        public CalibrationSettings Calibration = new CalibrationSettings();

        public event System.Action<Track> TrackCreated;
        public event System.Action<Track> TrackLost;

        readonly List<Track> _tracks = new List<Track>();
        public IReadOnlyList<Track> Tracks => _tracks;
        int _nextId = 1;

        readonly List<(float cost, int track, int obs)> _pairs = new List<(float, int, int)>();
        readonly HashSet<int> _usedTracks = new HashSet<int>();
        readonly HashSet<int> _usedObs = new HashSet<int>();

        public void Update(PoseFrame frame)
        {
            double t = frame.Timestamp;
            var people = frame.People;

            _pairs.Clear();
            for (int ti = 0; ti < _tracks.Count; ti++)
            {
                Vector2 c = _tracks[ti].Pose.TorsoCenter2D;
                for (int oi = 0; oi < people.Count; oi++)
                {
                    float d = Vector2.Distance(c, people[oi].TorsoCenter2D);
                    if (d <= MaxMatchDistance) _pairs.Add((d, ti, oi));
                }
            }
            _pairs.Sort((a, b) => a.cost.CompareTo(b.cost));

            _usedTracks.Clear();
            _usedObs.Clear();
            foreach (var tr in _tracks) tr.SeenThisFrame = false;

            foreach (var (_, ti, oi) in _pairs)
            {
                if (_usedTracks.Contains(ti) || _usedObs.Contains(oi)) continue;
                _usedTracks.Add(ti);
                _usedObs.Add(oi);
                Feed(_tracks[ti], people[oi], t);
            }

            for (int oi = 0; oi < people.Count; oi++)
            {
                if (_usedObs.Contains(oi)) continue;
                var tr = new Track(_nextId++, ImageFilter, WorldFilter, Calibration);
                _tracks.Add(tr);
                Feed(tr, people[oi], t);
                TrackCreated?.Invoke(tr);
            }

            for (int i = _tracks.Count - 1; i >= 0; i--)
            {
                var tr = _tracks[i];
                if (!tr.SeenThisFrame)
                {
                    tr.Calibration.Tick(null, null, t);
                    if (t - tr.LastSeen > LostTimeout)
                    {
                        _tracks.RemoveAt(i);
                        TrackLost?.Invoke(tr);
                    }
                }
            }
        }

        static void Feed(Track tr, PersonObservation obs, double t)
        {
            tr.Raw = obs;
            tr.Smoother.Update(obs, t);
            tr.LastSeen = t;
            tr.SeenThisFrame = true;
            // Stillness is judged on the raw pose (smoothing would hide motion); lengths come from the smoothed one.
            tr.Calibration.Tick(obs, tr.Pose, t);
        }
    }
}
