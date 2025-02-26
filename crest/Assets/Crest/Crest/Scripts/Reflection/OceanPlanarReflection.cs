// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

// This script originated from the unity standard assets. It has been modified heavily to be camera-centric (as opposed to
// geometry-centric) and assumes a single main camera which simplifies the code.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;


namespace Crest
{
    internal static class PreparedReflections
    {
        private static volatile RenderTexture _currentreflectiontexture;
        private static volatile int _referenceCameraInstanceId = -1;
        private static volatile KeyValuePair<int, RenderTexture>[] _collection = new KeyValuePair<int, RenderTexture>[0];
        public static RenderTexture GetRenderTexture(int camerainstanceid)
        {
            if (camerainstanceid == _referenceCameraInstanceId)
                return _currentreflectiontexture;

            var currentcollection = _collection;    //prevent crash if somebody change collection now in over thread, useless in unity now
            for (int i = 0; i < currentcollection.Length; i++)
            {
                if (currentcollection[i].Key == camerainstanceid)
                {
                    var texture = currentcollection[i].Value;
                    _currentreflectiontexture = texture;
                    _referenceCameraInstanceId = camerainstanceid;
                    return texture;
                }
            }
            return null;
        }

        public static void Remove(int camerainstanceid)  //remove element if exists
        {
            if (!GetRenderTexture(camerainstanceid)) return;
            _collection = _collection.Where(e => e.Key != camerainstanceid).ToArray(); //rebuild array without element
            _currentreflectiontexture = null;
            _referenceCameraInstanceId = -1;
        }

        public static void Register(int instanceId, RenderTexture reflectionTexture)
        {
            var currentcollection = _collection;
            for (var i = 0; i < currentcollection.Length; i++)
            {
                if (currentcollection[i].Key == instanceId)
                {
                    currentcollection[i] = new KeyValuePair<int, RenderTexture>(instanceId, reflectionTexture);
                    return;
                }
            }
            //rebuild with new element if not found
            _collection = currentcollection
                .Append(new KeyValuePair<int, RenderTexture>(instanceId, reflectionTexture)).ToArray();
        }
    }

    /// <summary>
    /// Attach to a camera to generate a reflection texture which can be sampled in the ocean shader.
    /// </summary>
    public class OceanPlanarReflection : MonoBehaviour
    {
        [SerializeField] LayerMask _reflectionLayers = 1;
        [SerializeField] bool _disablePixelLights = true;
        [SerializeField] int _textureSize = 256;
        [SerializeField] float _clipPlaneOffset = 0.07f;
        [SerializeField] bool _hdr = true;
        [SerializeField] bool _stencil = false;
        [SerializeField] bool _hideCameraGameobject = true;
        [SerializeField] bool _allowMSAA = false;           //allow MSAA on reflection camera
        [SerializeField] float _farClipPlane = 1000;             //far clip plane for reflection camera on all layers
        [SerializeField] bool _forceForwardRenderingPath = true;
        [SerializeField] CameraClearFlags _clearFlags = CameraClearFlags.Color;

        /// <summary>
        /// Refresh reflection every x frames(1-every frame)
        /// </summary>
        [SerializeField] int RefreshPerFrames = 1;

        /// <summary>
        /// To relax OceanPlanarReflection refresh to different frames need to set different values for each script
        /// </summary>
        [SerializeField] int _frameRefreshOffset = 0;

        RenderTexture _reflectionTexture;
        Camera _camViewpoint;
        Camera _camReflections;
        private long _lastRefreshOnFrame = -1;
        float[] _cullDistances;
        private void Start()
        {
            _camViewpoint = GetComponent<Camera>();
            if (!_camViewpoint)
            {
                Debug.LogWarning("Disabling planar reflections as no camera found on gameobject to generate reflection from.", this);
                enabled = false;
                return;
            }

            // This is anyway called in OnPreRender, but was required here as there was a black reflection
            // for a frame without this earlier setup call.
            CreateWaterObjects(_camViewpoint);

#if UNITY_EDITOR
            if (!OceanRenderer.Instance.OceanMaterial.IsKeywordEnabled("_PLANARREFLECTIONS_ON"))
            {
                Debug.LogWarning("Planar reflections are not enabled on the current ocean material and will not be visible.", this);
            }
#endif
        }

        bool RequestRefresh(long frame)
        {
            if (_lastRefreshOnFrame <= 0 || RefreshPerFrames < 2)
                return true;    //not refreshed before or refresh every frame, not check frame counter
            return Math.Abs(_frameRefreshOffset) % RefreshPerFrames == frame % RefreshPerFrames;
        }

        void Refreshed(long currentframe)
        {
            _lastRefreshOnFrame = currentframe;
        }
        private void OnPreRender()
        {
            if (!RequestRefresh(Time.renderedFrameCount))
                return; //skip if not need to refresh on this frame

            CreateWaterObjects(_camViewpoint);

            if (!_camReflections)
            {
                return;
            }

            // find out the reflection plane: position and normal in world space
            Vector3 planePos = OceanRenderer.Instance.transform.position;
            Vector3 planeNormal = Vector3.up;

            // Optionally disable pixel lights for reflection/refraction
            int oldPixelLightCount = QualitySettings.pixelLightCount;
            if (_disablePixelLights)
            {
                QualitySettings.pixelLightCount = 0;
            }

            UpdateCameraModes(_camViewpoint, _camReflections);

            // Reflect camera around reflection plane
            float d = -Vector3.Dot(planeNormal, planePos) - _clipPlaneOffset;
            Vector4 reflectionPlane = new Vector4(planeNormal.x, planeNormal.y, planeNormal.z, d);

            Matrix4x4 reflection = Matrix4x4.zero;
            CalculateReflectionMatrix(ref reflection, reflectionPlane);
            Vector3 newpos = reflection.MultiplyPoint(_camViewpoint.transform.position);
            _camReflections.worldToCameraMatrix = _camViewpoint.worldToCameraMatrix * reflection;

            // Setup oblique projection matrix so that near plane is our reflection
            // plane. This way we clip everything below/above it for free.
            Vector4 clipPlane = CameraSpacePlane(_camReflections, planePos, planeNormal, 1.0f);
            _camReflections.projectionMatrix = _camViewpoint.CalculateObliqueMatrix(clipPlane);

            // Set custom culling matrix from the current camera
            _camReflections.cullingMatrix = _camViewpoint.projectionMatrix * _camViewpoint.worldToCameraMatrix;

            _camReflections.targetTexture = _reflectionTexture;

            // Invert culling because view is mirrored
            bool oldCulling = GL.invertCulling;
            GL.invertCulling = !oldCulling;

            _camReflections.transform.position = newpos;
            Vector3 euler = _camViewpoint.transform.eulerAngles;
            _camReflections.transform.eulerAngles = new Vector3(-euler.x, euler.y, euler.z);
            _camReflections.cullingMatrix = _camReflections.projectionMatrix * _camReflections.worldToCameraMatrix;

            ForceDistanceCulling(_farClipPlane);
            
            _camReflections.Render();

            GL.invertCulling = oldCulling;

            // Restore pixel light count
            if (_disablePixelLights)
            {
                QualitySettings.pixelLightCount = oldPixelLightCount;
            }

            Refreshed(Time.renderedFrameCount); //remember this frame as last refreshed
        }


        /// <summary>
        /// Limit render distance for reflection camera for first 32 layers
        /// </summary>
        /// <param name="farClipPlane">reflection far clip distance</param>
        private void ForceDistanceCulling(float farClipPlane)
        {
            if (_cullDistances == null)
                _cullDistances = new float[32];
            for (var i = 0; i < _cullDistances.Length; i++)
            {
                _cullDistances[i] = farClipPlane; //the culling distance
            }
            _camReflections.layerCullDistances = _cullDistances;
            _camReflections.layerCullSpherical = true;
        }

        void UpdateCameraModes(Camera src, Camera dest)
        {
            // set water camera to clear the same way as current camera
            dest.renderingPath = _forceForwardRenderingPath ? RenderingPath.Forward : src.renderingPath;
            dest.backgroundColor = new Color(0f, 0f, 0f, 0f);
            dest.clearFlags = _clearFlags;
            if (_clearFlags == CameraClearFlags.Skybox)
            {
                Skybox sky = src.GetComponent<Skybox>();
                Skybox mysky = dest.GetComponent<Skybox>();
                if (!sky || !sky.material)
                {
                    mysky.enabled = false;
                }
                else
                {
                    mysky.enabled = true;
                    mysky.material = sky.material;
                }
            }

            // update other values to match current camera.
            // even if we are supplying custom camera&projection matrices,
            // some of values are used elsewhere (e.g. skybox uses far plane)

            dest.farClipPlane = src.farClipPlane;
            dest.nearClipPlane = src.nearClipPlane;
            dest.orthographic = src.orthographic;
            dest.fieldOfView = src.fieldOfView;
            dest.orthographicSize = src.orthographicSize;
            dest.allowMSAA = _allowMSAA;
            dest.aspect = src.aspect;
        }

        // On-demand create any objects we need for water
        void CreateWaterObjects(Camera currentCamera)
        {
            // Reflection render texture
            if (!_reflectionTexture || _reflectionTexture.width != _textureSize)
            {
                if (_reflectionTexture)
                {
                    DestroyImmediate(_reflectionTexture);
                }

                var format = _hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                Debug.Assert(SystemInfo.SupportsRenderTextureFormat(format), "The graphics device does not support the render texture format " + format.ToString());
                _reflectionTexture = new RenderTexture(_textureSize, _textureSize, _stencil ? 24 : 16, format)
                {
                    name = "__WaterReflection" + GetHashCode(),
                    isPowerOfTwo = true,
                    hideFlags = HideFlags.DontSave
                };
                _reflectionTexture.Create();
                PreparedReflections.Register(currentCamera.GetHashCode(), _reflectionTexture);
            }

            // Camera for reflection
            if (!_camReflections)
            {
                GameObject go = new GameObject("Water Refl Cam");
                _camReflections = go.AddComponent<Camera>();
                _camReflections.enabled = false;
                _camReflections.transform.position = transform.position;
                _camReflections.transform.rotation = transform.rotation;
                _camReflections.cullingMask = _reflectionLayers;
                _camReflections.gameObject.AddComponent<Skybox>();
                _camReflections.gameObject.AddComponent<FlareLayer>();

                if (_hideCameraGameobject)
                {
                    go.hideFlags = HideFlags.HideAndDontSave;
                }
            }

        }

        // Given position/normal of the plane, calculates plane in camera space.
        Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign)
        {
            Vector3 offsetPos = pos + normal * _clipPlaneOffset;
            Matrix4x4 m = cam.worldToCameraMatrix;
            Vector3 cpos = m.MultiplyPoint(offsetPos);
            Vector3 cnormal = m.MultiplyVector(normal).normalized * sideSign;
            return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
        }

        // Calculates reflection matrix around the given plane
        static void CalculateReflectionMatrix(ref Matrix4x4 reflectionMat, Vector4 plane)
        {
            reflectionMat.m00 = (1F - 2F * plane[0] * plane[0]);
            reflectionMat.m01 = (-2F * plane[0] * plane[1]);
            reflectionMat.m02 = (-2F * plane[0] * plane[2]);
            reflectionMat.m03 = (-2F * plane[3] * plane[0]);

            reflectionMat.m10 = (-2F * plane[1] * plane[0]);
            reflectionMat.m11 = (1F - 2F * plane[1] * plane[1]);
            reflectionMat.m12 = (-2F * plane[1] * plane[2]);
            reflectionMat.m13 = (-2F * plane[3] * plane[1]);

            reflectionMat.m20 = (-2F * plane[2] * plane[0]);
            reflectionMat.m21 = (-2F * plane[2] * plane[1]);
            reflectionMat.m22 = (1F - 2F * plane[2] * plane[2]);
            reflectionMat.m23 = (-2F * plane[3] * plane[2]);

            reflectionMat.m30 = 0F;
            reflectionMat.m31 = 0F;
            reflectionMat.m32 = 0F;
            reflectionMat.m33 = 1F;
        }

        private void OnDisable()
        {
            if (_camViewpoint != null)
            {
                PreparedReflections.Remove(_camViewpoint.GetHashCode());
            }

            // Cleanup all the objects we possibly have created
            if (_reflectionTexture)
            {
                Destroy(_reflectionTexture);
                _reflectionTexture = null;
            }
            if (_camReflections)
            {
                Destroy(_camReflections.gameObject);
                _camReflections = null;
            }
        }
    }
}
