﻿// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

// Shout out to @holdingjason who posted a first version of this script here: https://github.com/huwb/crest-oceanrender/pull/100

using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace Crest
{
    /// <summary>
    /// Boat physics by sampling at multiple probe points.
    /// </summary>
    public class BoatProbes : FloatingObjectBase
    {
        [Header("Forces")]
        [Tooltip("Override RB center of mass, in local space."), SerializeField]
        Vector3 _centerOfMass = Vector3.zero;
        [SerializeField, FormerlySerializedAs("ForcePoints")]
        FloaterForcePoints[] _forcePoints = new FloaterForcePoints[] { };
        [SerializeField]
        float _forceHeightOffset = 0f;
        [SerializeField]
        float _forceMultiplier = 10f;
        [SerializeField]
        float _minSpatialLength = 12f;
        [SerializeField, Range(0, 1)]
        float _turningHeel = 0.35f;

        [Header("Drag")]
        [SerializeField]
        float _dragInWaterUp = 3f;
        [SerializeField]
        float _dragInWaterRight = 2f;
        [SerializeField]
        float _dragInWaterForward = 1f;

        [Header("Control")]
        [SerializeField, FormerlySerializedAs("EnginePower")]
        float _enginePower = 7;
        [SerializeField, FormerlySerializedAs("TurnPower")]
        float _turnPower = 0.5f;
        [SerializeField]
        bool _playerControlled = true;
        [SerializeField]
        float _engineBias = 0f;
        [SerializeField]
        float _turnBias = 0f;


        private const float WATER_DENSITY = 1000;

        public override Vector3 Velocity => _rb.velocity;

        Rigidbody _rb;

        Vector3 _displacementToObject = Vector3.zero;
        public override Vector3 CalculateDisplacementToObject() { return _displacementToObject; }

        public override float ObjectWidth { get { return _minSpatialLength; } }
        public override bool InWater { get { return true; } }

        SamplingData _samplingData = new SamplingData();
        SamplingData _samplingDataFlow = new SamplingData();

        Rect _localSamplingAABB;
        float _totalWeight;

        Vector3[] _queryPoints;
        Vector3[] _queryResultDisps;
        Vector3[] _queryResultVels;

        SampleFlowHelper _sampleFlowHelper = new SampleFlowHelper();

        private void Start()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.centerOfMass = _centerOfMass;

            if (OceanRenderer.Instance == null)
            {
                enabled = false;
                return;
            }

            _localSamplingAABB = ComputeLocalSamplingAABB();

            CalcTotalWeight();

            _queryPoints = new Vector3[_forcePoints.Length + 1];
            _queryResultDisps = new Vector3[_forcePoints.Length + 1];
            _queryResultVels = new Vector3[_forcePoints.Length + 1];
        }

        void CalcTotalWeight()
        {
            _totalWeight = 0f;
            foreach (var pt in _forcePoints)
            {
                _totalWeight += pt._weight;
            }
        }

        private void FixedUpdate()
        {
#if UNITY_EDITOR
            // Sum weights every frame when running in editor in case weights are edited in the inspector.
            CalcTotalWeight();
#endif

            // Trigger processing of displacement textures that have come back this frame. This will be processed
            // anyway in Update(), but FixedUpdate() is earlier so make sure it's up to date now.
            if (OceanRenderer.Instance._simSettingsAnimatedWaves.CollisionSource == SimSettingsAnimatedWaves.CollisionSources.OceanDisplacementTexturesGPU && GPUReadbackDisps.Instance)
            {
                GPUReadbackDisps.Instance.ProcessRequests();
            }

            var collProvider = OceanRenderer.Instance.CollisionProvider;
            var thisRect = GetWorldAABB();
            if (!collProvider.GetSamplingData(ref thisRect, _minSpatialLength, _samplingData))
            {
                // No collision coverage for the sample area, in this case use the null provider.
                collProvider = CollProviderNull.Instance;
            }

            // Do queries
            UpdateWaterQueries(collProvider);

            _displacementToObject = _queryResultDisps[_forcePoints.Length];
            var undispPos = transform.position - _queryResultDisps[_forcePoints.Length];
            undispPos.y = OceanRenderer.Instance.SeaLevel;

            var waterSurfaceVel = _queryResultVels[_forcePoints.Length];

            if(QueryFlow.Instance)
            {
                _sampleFlowHelper.Init(transform.position, _minSpatialLength);
                Vector2 surfaceFlow = Vector2.zero;
                _sampleFlowHelper.Sample(ref surfaceFlow);
                waterSurfaceVel += new Vector3(surfaceFlow.x, 0, surfaceFlow.y);
            }

            // Buoyancy
            FixedUpdateBuoyancy(collProvider);
            FixedUpdateDrag(collProvider, waterSurfaceVel);
            FixedUpdateEngine();

            collProvider.ReturnSamplingData(_samplingData);
        }

        void UpdateWaterQueries(ICollProvider collProvider)
        {
            // Update query points
            for (int i = 0; i < _forcePoints.Length; i++)
            {
                _queryPoints[i] = transform.TransformPoint(_forcePoints[i]._offsetPosition + new Vector3(0, _centerOfMass.y, 0));
            }
            _queryPoints[_forcePoints.Length] = transform.position;

            collProvider.Query(GetHashCode(), _samplingData, _queryPoints, _queryResultDisps, null, _queryResultVels);
        }

        void FixedUpdateEngine()
        {
            var forcePosition = _rb.position;

            var forward = _engineBias;
            if (_playerControlled) forward += Input.GetAxis("Vertical");
            _rb.AddForceAtPosition(transform.forward * _enginePower * forward, forcePosition, ForceMode.Acceleration);

            var sideways = _turnBias;
            if (_playerControlled) sideways += (Input.GetKey(KeyCode.A) ? -1f : 0f) + (Input.GetKey(KeyCode.D) ? 1f : 0f);
            var rotVec = transform.up + _turningHeel * transform.forward;
            _rb.AddTorque(rotVec * _turnPower * sideways, ForceMode.Acceleration);
        }

        void FixedUpdateBuoyancy(ICollProvider collProvider)
        {
            var archimedesForceMagnitude = WATER_DENSITY * Mathf.Abs(Physics.gravity.y);

            for (int i = 0; i < _forcePoints.Length; i++)
            {
                var waterHeight = OceanRenderer.Instance.SeaLevel + _queryResultDisps[i].y;
                var heightDiff = waterHeight - _queryPoints[i].y;
                if (heightDiff > 0)
                {
                    _rb.AddForceAtPosition(archimedesForceMagnitude * heightDiff * Vector3.up * _forcePoints[i]._weight * _forceMultiplier / _totalWeight, _queryPoints[i]);
                }
            }
        }

        void FixedUpdateDrag(ICollProvider collProvider, Vector3 waterSurfaceVel)
        {
            // Apply drag relative to water
            var _velocityRelativeToWater = _rb.velocity - waterSurfaceVel;

            var forcePosition = _rb.position + _forceHeightOffset * Vector3.up;
            _rb.AddForceAtPosition(Vector3.up * Vector3.Dot(Vector3.up, -_velocityRelativeToWater) * _dragInWaterUp, forcePosition, ForceMode.Acceleration);
            _rb.AddForceAtPosition(transform.right * Vector3.Dot(transform.right, -_velocityRelativeToWater) * _dragInWaterRight, forcePosition, ForceMode.Acceleration);
            _rb.AddForceAtPosition(transform.forward * Vector3.Dot(transform.forward, -_velocityRelativeToWater) * _dragInWaterForward, forcePosition, ForceMode.Acceleration);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawCube(transform.TransformPoint(_centerOfMass), Vector3.one * 0.25f);

            for (int i = 0; i < _forcePoints.Length; i++)
            {
                var point = _forcePoints[i];

                var transformedPoint = transform.TransformPoint(point._offsetPosition + new Vector3(0, _centerOfMass.y, 0));

                Gizmos.color = Color.red;
                Gizmos.DrawCube(transformedPoint, Vector3.one * 0.5f);
            }

            var worldAABB = GetWorldAABB();
            new Bounds(new Vector3(worldAABB.center.x, 0f, worldAABB.center.y), Vector3.right * worldAABB.width + Vector3.forward * worldAABB.height).DebugDraw();
        }

        Rect ComputeLocalSamplingAABB()
        {
            if (_forcePoints.Length == 0) return new Rect();

            float xmin = _forcePoints[0]._offsetPosition.x;
            float zmin = _forcePoints[0]._offsetPosition.z;
            float xmax = xmin, zmax = zmin;
            for (int i = 1; i < _forcePoints.Length; i++)
            {
                float x = _forcePoints[i]._offsetPosition.x, z = _forcePoints[i]._offsetPosition.z;
                xmin = Mathf.Min(xmin, x); xmax = Mathf.Max(xmax, x);
                zmin = Mathf.Min(zmin, z); zmax = Mathf.Max(zmax, z);
            }

            return Rect.MinMaxRect(xmin, zmin, xmax, zmax);
        }

        Rect GetWorldAABB()
        {
            Bounds b = new Bounds(transform.position, Vector3.one);
            b.Encapsulate(transform.TransformPoint(new Vector3(_localSamplingAABB.xMin, 0f, _localSamplingAABB.yMin)));
            b.Encapsulate(transform.TransformPoint(new Vector3(_localSamplingAABB.xMin, 0f, _localSamplingAABB.yMax)));
            b.Encapsulate(transform.TransformPoint(new Vector3(_localSamplingAABB.xMax, 0f, _localSamplingAABB.yMin)));
            b.Encapsulate(transform.TransformPoint(new Vector3(_localSamplingAABB.xMax, 0f, _localSamplingAABB.yMax)));
            return Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z);
        }

        private void OnDisable()
        {
            if (QueryDisplacements.Instance)
            {
                QueryDisplacements.Instance.RemoveQueryPoints(GetHashCode());
            }
        }
    }

    [Serializable]
    public class FloaterForcePoints
    {
        [FormerlySerializedAs("_factor")]
        public float _weight = 1f;

        public Vector3 _offsetPosition;
    }
}
