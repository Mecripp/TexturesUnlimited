﻿using System.Collections.Generic;
using UnityEngine;
using KSP.UI.Screens;
using System.IO;

namespace KSPShaderTools
{
    [KSPAddon(KSPAddon.Startup.FlightAndEditor, false)]
    public class ReflectionManager : MonoBehaviour
    {

        #region CONSTANTS

        public const int galaxyMask = 1 << 18;
        public const int atmosphereMask = (1 << 9) | (1 << 23);
        public const int scaledSpaceMask = 1 << 10;
        public const int sceneryMask = (1 << 4) | (1 << 15);
        public const int fullSceneMask = ~0;

        #endregion

        #region CONFIG FIELDS

        /// <summary>
        /// Should static reflection maps be used?
        /// If true, reflection maps will only be rendered a single time on the scene initialization.
        /// If false, reflection maps will be updated at runtime with a frequency/delay specified by further config settings
        /// </summary>
        public bool useStaticMaps = false;

        /// <summary>
        /// Should a reflection probe be added per-part?
        /// If true, reflections are done on a per-part basis.
        /// If false, reflections are done on a per-vessel basis.
        /// </summary>
        public bool perPartMaps = false;

        /// <summary>
        /// Number of frames inbetween reflection map updates.
        /// </summary>
        public int mapUpdateSpacing = 60;

        /// <summary>
        /// Number of faces to happen on any given update.
        /// </summary>
        public int numberOfFaces = 1;

        /// <summary>
        /// Size of the rendered reflection map.  Higher resolutions result in higher fidelity reflections, but at a much higher run-time cost.
        /// Must be a power-of-two size; e.g. 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048.
        /// </summary>
        public int envMapSize = 512;

        /// <summary>
        /// Layer to use for skybox hack
        /// </summary>
        public int skyboxLayer = 26;

        #endregion

        #region DEBUG FIELDS

        //set through the reflection debug GUI

        public bool renderGalaxy = true;        
        public bool renderScaled = true;        
        public bool renderAtmo = true;        
        public bool renderScenery = true;

        public bool reflectionsEnabled = true;

        #endregion

        #region INTERNAL FIELDS

        public GameObject cameraObject;
        public Camera reflectionCamera;

        /// <summary>
        /// The reflection data for inside of the (current) editor.  Should be rebuilt whenever the editor is initialized, closed, or change
        /// </summary>
        public EditorReflectionData editorReflectionData;

        public VesselReflectionData vesselReflectionData;

        //Mod interop stuff

        public bool eveInstalled = true;//TODO -- load this value from config
        public CameraAlphaFix eveCameraFix;

        //internal data -- event handling, app-launcher button and debug-GUI handling

        private bool renderedEditor = false;
        private int editorDelay = 0;
        private int editorTarget = 2;
        private bool export = false;
        private bool debug = false;

        internal List<ReflectionPass> renderStack = new List<ReflectionPass>();
        
        private ReflectionDebugGUI gui;
        private static ApplicationLauncherButton debugAppButton;

        private static Shader skyboxShader;

        private static ReflectionManager instance;

        public static ReflectionManager Instance
        {
            get
            {
                return instance;
            }
        }

        #endregion

        #region LIFECYCLE METHODS

        public void Awake()
        {
            MonoBehaviour.print("ReflectionManager Awake()");
            instance = this;

            if (renderStack.Count <= 0)
            {
                renderStack.Add(ReflectionPass.GALAXY);
                renderStack.Add(ReflectionPass.ATMOSPHERE);
                renderStack.Add(ReflectionPass.SCALED);
                renderStack.Add(ReflectionPass.SCENERY);
            }

            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("REFLECTION_CONFIG");
            if (nodes == null || nodes.Length < 1)
            {
                reflectionsEnabled = false;
                return;
            }
            ConfigNode node = nodes[0];
            MonoBehaviour.print("SSTUReflectionManager - Loading reflection configuration: \n" + node.ToString());
            reflectionsEnabled = node.GetBoolValue("enabled", false);
            envMapSize = node.GetIntValue("resolution", envMapSize);
            mapUpdateSpacing = node.GetIntValue("interval", mapUpdateSpacing);
            numberOfFaces = node.GetIntValue("faces", numberOfFaces);
            eveInstalled = node.GetBoolValue("eveInstalled", false);
            debug = node.GetBoolValue("debug", false);
            export = node.GetBoolValue("exportDebugCubes", false);

            init();

            Texture2D tex;
            if (debugAppButton == null && debug)//static reference; track if the button was EVER created, as KSP keeps them even if the addon is destroyed
            {                
                //create a new button
                tex = GameDatabase.Instance.GetTexture("Squad/PartList/SimpleIcons/RDIcon_fuelSystems-highPerformance", false);
                debugAppButton = ApplicationLauncher.Instance.AddModApplication(debugGuiEnable, debugGuiDisable, null, null, null, null, ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.SPH | ApplicationLauncher.AppScenes.VAB, tex);
            }
            else if(debugAppButton != null)
            {
                //reseat callback refs to the ones from THIS instance of the KSPAddon (old refs were stale, pointing to methods for a deleted class instance)
                debugAppButton.onTrue = debugGuiEnable;
                debugAppButton.onFalse = debugGuiDisable;
            }
        }

        /// <summary>
        /// Unity per-frame update method.  Should update any reflection maps that need updating.
        /// </summary>
        public void Update()
        {
            if (!reflectionsEnabled) { return; }
            updateReflections();

            //TODO convolution on cubemap
            //https://seblagarde.wordpress.com/2012/06/10/amd-cubemapgen-for-physically-based-rendering/
            //http://codeflow.org/entries/2011/apr/18/advanced-webgl-part-3-irradiance-environment-map/
            //https://developer.nvidia.com/gpugems/GPUGems2/gpugems2_chapter10.html
            //https://gist.github.com/Farfarer/5664694
            //https://codegists.com/code/render-cubemap-unity/

            //TODO conversion of existing textures:
            //https://www.marmoset.co/posts/pbr-texture-conversion/

            //https://forum.unity.com/threads/directly-draw-a-cubemap-rendertexture.296236/
            //In function Graphics.SetRenderTarget(), you can select cubemap face. 

            //convolution processing:
            //1.) CPU based
            //      Render to standard Cubemap
            //      Sample and convolve in CPU
            //      Update Cubemap from convolved data
            //      
            //2.) GPU based
            //      Shader has single Cubemap input from raw rendered
            //      Shader samples cubemap, renders out to standard surface rendertexture, one face at a time
            //      Recompose the 6x render textures back into a single Cubemap (with MIPs)            
        }

        private void debugGuiEnable()
        {
            gui = gameObject.AddComponent<ReflectionDebugGUI>();
        }

        public void debugGuiDisable()
        {
            GameObject.Destroy(gui);
            gui = null;
        }

        public void OnDestroy()
        {
            MonoBehaviour.print("SSTUReflectionManager OnDestroy()");
            if (instance == this)
            {
                instance = null;
            }
            if (gui != null)
            {
                GameObject.Destroy(gui);
                gui = null;
            }
            //TODO proper resource cleanup
            //TODO do materials and render textures need to be released?
        }

        #endregion

        #region FUNCTIONAL METHODS

        private void init()
        {
            MonoBehaviour.print("SSTUReflectionManager init()");
            if (cameraObject == null)
            {
                cameraObject = new GameObject("TRReflectionCamera");
                reflectionCamera = cameraObject.AddComponent<Camera>();
                eveCameraFix = cameraObject.AddComponent<CameraAlphaFix>();
                reflectionCamera.enabled = false;
                MonoBehaviour.print("SSTUReflectionManager created camera: "+reflectionCamera);
            }
            if (skyboxShader == null)
            {
                skyboxShader = KSPShaderTools.KSPShaderLoader.getShader("SSTU/Skybox/Cubemap");
                if (skyboxShader == null)
                {
                    MonoBehaviour.print("ERROR: SSTUReflectionManager - Could not find skybox shader.");
                }
            }
            if (HighLogic.LoadedSceneIsEditor)
            {
                ReflectionProbeData data = createProbe();
                data.reflectionSphere.transform.position = new Vector3(0, 10, 0);
                editorReflectionData = new EditorReflectionData(data);
                MonoBehaviour.print("SSTUReflectionManager created editor reflection data: " + data + " :: " +editorReflectionData);
            }
            else if (HighLogic.LoadedSceneIsFlight)
            {
                ReflectionProbeData data = createProbe();
                vesselReflectionData = new VesselReflectionData(data);
                MonoBehaviour.print("SSTUReflectionManager created flight reflection data: " + data + " :: " + vesselReflectionData);
            }

            //TODO -- replace with custom baked skybox...
            //use in areas where other reflection probes don't make sense (space?)
            //RenderSettings.customReflection = customCubemap;

            //TODO -- pre-bake cubemap to use as the custom skybox in the reflection probe camera; this can be higher res and updated far less often (every couple of seconds?)
        }

        public void updateReflections(bool force = false)
        {
            reflectionCamera.enabled = true;
            reflectionCamera.clearFlags = CameraClearFlags.Depth;
            if (editorReflectionData != null)
            {
                if (!renderedEditor || force)
                {
                    if (editorDelay >= editorTarget)
                    {
                        renderedEditor = true;
                    }
                    editorDelay++;
                    if (force || renderedEditor)
                    {
                        MonoBehaviour.print("updating editor reflection");
                        renderFullCube(editorReflectionData.probeData.renderedCube, new Vector3(0, 10, 0));
                        updateProbe(editorReflectionData.probeData);
                    }
                    if (force && export)
                    {
                        exportCubemap(editorReflectionData.probeData.renderedCube, "editorReflect");
                    }
                }
            }
            else if(vesselReflectionData!=null)//unpossible, but w/e
            {
                if (FlightIntegrator.ActiveVesselFI != null && FlightIntegrator.ActiveVesselFI.Vessel != null && FlightIntegrator.ActiveVesselFI.Vessel.loaded)
                {
                    VesselReflectionData d = this.vesselReflectionData;
                    Vessel v = FlightIntegrator.ActiveVesselFI.Vessel;
                    d.probeData.reflectionSphere.transform.position = v.transform.position;
                    if (force)
                    {
                        renderFullCube(d.probeData.renderedCube, v.transform.position);
                        updateProbe(d.probeData);
                        if (export)
                        {
                            exportCubemap(d.probeData.renderedCube, "vesselReflect-" + v.name);
                        }
                    }
                    else
                    {
                        d.probeData.updateTime++;
                        if (d.probeData.updateTime >= mapUpdateSpacing)
                        {
                            for (int i = 0; i < numberOfFaces && d.probeData.updateFace < 6; i++)
                            {
                                renderFace(d.probeData.renderedCube, d.probeData.updateFace, v.transform.position);
                                d.probeData.updateFace++;
                            }
                            if (d.probeData.updateFace >= 6)
                            {
                                updateProbe(d.probeData);
                                d.probeData.updateTime = 0;
                                d.probeData.updateFace = 0;
                            }
                        }
                    }
                }
            }
            reflectionCamera.enabled = false;
        }

        #endregion

        #region UPDATE UTILITY METHODS

        private void updateProbe(ReflectionProbeData data)
        {
            data.skyboxMateral.SetTexture("_Tex", data.renderedCube);
            data.reflectionSphere.transform.rotation = Quaternion.identity;//align to world space
            data.render.material = data.skyboxMateral;
            data.probe.RenderProbe();
        }

        private void renderFullCube(RenderTexture envMap, Vector3 partPos)
        {
            for (int face = 0; face < 6; face++)
            {
                renderFace(envMap, face, partPos);
            }
        }

        private void renderFace(RenderTexture envMap, int face, Vector3 partPos)
        {
            //TODO -- scaled and atmo need to be rendered in oposite order while in orbit
            //or something....

            //TODO -- investigate splitting the near/far rendering of scenery to reduce the massively massive far-clip plane.

            float nearClip = 0.3f;
            float farClip = 3.0e7f;
            int faceMask = 1 << face;

            int len = renderStack.Count;
            ReflectionPass pass;
            for (int i = 0; i < len; i++)
            {
                pass = renderStack[i];
                switch (pass)
                {
                    case ReflectionPass.GALAXY:
                        if (renderGalaxy)
                        {
                            //galaxy
                            renderCubeFace(envMap, faceMask, GalaxyCubeControl.Instance.transform.position, galaxyMask, nearClip, farClip);
                        }
                        break;
                    case ReflectionPass.SCALED:
                        if (renderScaled)
                        {
                            //scaled space
                            renderCubeFace(envMap, faceMask, ScaledSpace.Instance.transform.position, scaledSpaceMask, nearClip, farClip);
                        }
                        break;
                    case ReflectionPass.SCENERY:
                        if (renderScenery)
                        {
                            //scene
                            eveCameraFix.overwriteAlpha = eveInstalled;
                            renderCubeFace(envMap, faceMask, partPos, sceneryMask, nearClip, farClip);
                            eveCameraFix.overwriteAlpha = false;
                        }
                        break;
                    case ReflectionPass.ATMOSPHERE:
                        if (renderAtmo)
                        {
                            //atmo
                            renderCubeFace(envMap, faceMask, partPos, atmosphereMask, nearClip, farClip);
                        }
                        break;
                    default:
                        break;
                }
            }
        }

        private void renderCubeFace(RenderTexture envMap, int faceMask, Vector3 cameraPos, int layerMask, float nearClip, float farClip)
        {
            cameraSetup(cameraPos, layerMask, nearClip, farClip);
            reflectionCamera.RenderToCubemap(envMap, faceMask);
        }

        private void cameraSetup(Vector3 pos, int mask, float near, float far)
        {
            reflectionCamera.transform.position = pos;
            reflectionCamera.cullingMask = mask;
            reflectionCamera.nearClipPlane = near;
            reflectionCamera.farClipPlane = far;
        }

        private ReflectionProbeData createProbe()
        {
            GameObject refSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            GameObject.Destroy(refSphere.GetComponent<Collider>());
            refSphere.transform.localScale = new Vector3(10, 10, 10);
            refSphere.layer = skyboxLayer;
            refSphere.name = "SSTUReflectionProbe";

            MeshRenderer rend = refSphere.GetComponent<MeshRenderer>();
            Material mat = new Material(skyboxShader);
            rend.material = mat;//still has to be updated later
            ReflectionProbe probe = createProbe(refSphere);
            RenderTexture tex = createTexture(envMapSize);
            ReflectionProbeData data = new ReflectionProbeData(refSphere, rend, mat, probe, tex);
            data.updateTime = mapUpdateSpacing;//force update on the first frame it is 'loaded'
            return data;
        }

        private ReflectionProbe createProbe(GameObject host)
        {
            ReflectionProbe pr = host.AddComponent<ReflectionProbe>();
            pr.type = UnityEngine.Rendering.ReflectionProbeType.Cube;
            pr.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            pr.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
            pr.clearFlags = UnityEngine.Rendering.ReflectionProbeClearFlags.SolidColor;
            pr.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.IndividualFaces;
            pr.hdr = false;
            pr.size = new Vector3(2000, 2000, 2000);
            pr.resolution = envMapSize;
            pr.enabled = true;
            pr.cullingMask = 1 << skyboxLayer;
            return pr;
        }

        private RenderTexture createTexture(int size)
        {
            RenderTexture tex = new RenderTexture(size, size, 24);
            tex.dimension = UnityEngine.Rendering.TextureDimension.Cube;
            tex.format = RenderTextureFormat.ARGB32;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.generateMips = false;
            return tex;
        }

        #endregion

        #region DEBUG CUBE RENDERING

        public void renderDebugCubes()
        {
            int size = envMapSize * 4;
            Cubemap map = new Cubemap(size, TextureFormat.ARGB32, false);
            Texture2D exportTex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Vector3 pos = HighLogic.LoadedSceneIsEditor ? new Vector3(0, 10, 0) : FlightIntegrator.ActiveVesselFI.Vessel.transform.position;
            exportCubes(map, pos);
        }

        public void renderDebugLayers()
        {
            int size = envMapSize * 4;
            Cubemap map = new Cubemap(size, TextureFormat.RGB24, false);
            Texture2D exportTex = new Texture2D(size, size, TextureFormat.RGB24, false);
            Vector3 pos = HighLogic.LoadedSceneIsEditor ? new Vector3(0, 10, 0) : FlightIntegrator.ActiveVesselFI.Vessel.transform.position;

            reflectionCamera.enabled = true;
            float nearClip = reflectionCamera.nearClipPlane;
            float farClip = 3.0e7f;

            reflectionCamera.clearFlags = CameraClearFlags.SolidColor;
            Color bg = reflectionCamera.backgroundColor;
            reflectionCamera.backgroundColor = Color.clear;

            int len = 32;
            int mask = 0;
            for (int i = 0; i < len; i++)
            {
                mask = 1 << i;
                renderCube(map, pos, mask, nearClip, farClip);
                exportCubemap(map, "layer"+i);
            }
            reflectionCamera.clearFlags = CameraClearFlags.Depth;
            reflectionCamera.enabled = false;
        }

        private void exportCubes(Cubemap debugCube, Vector3 pos)
        {
            reflectionCamera.enabled = true;
            float nearClip = reflectionCamera.nearClipPlane;
            float farClip = 3.0e7f;

            reflectionCamera.clearFlags = CameraClearFlags.SolidColor;
            Color bg = reflectionCamera.backgroundColor;
            reflectionCamera.backgroundColor = Color.clear;

            renderCube(debugCube, GalaxyCubeControl.Instance.transform.position, galaxyMask, nearClip, farClip);
            exportCubemap(debugCube, "galaxy");
            renderCube(debugCube, ScaledSpace.Instance.transform.position, scaledSpaceMask, nearClip, farClip);
            exportCubemap(debugCube, "scaled");
            renderCube(debugCube, pos, sceneryMask, nearClip, farClip);
            exportCubemap(debugCube, "scene");
            renderCube(debugCube, pos, atmosphereMask, nearClip, farClip);
            exportCubemap(debugCube, "skybox");
            renderCube(debugCube, pos, fullSceneMask, nearClip, farClip);
            exportCubemap(debugCube, "full");
            reflectionCamera.backgroundColor = bg;

            //export the same as the active reflection setup
            reflectionCamera.clearFlags = CameraClearFlags.Depth;
            for (int i = 0; i < 6; i++)
            {
                CubemapFace face = (CubemapFace)i;

                if (renderGalaxy)
                {
                    //galaxy
                    renderCubeFace(debugCube, face, GalaxyCubeControl.Instance.transform.position, galaxyMask, nearClip, farClip);
                }
                if (renderScaled)
                {
                    //scaled space
                    renderCubeFace(debugCube, face, ScaledSpace.Instance.transform.position, scaledSpaceMask, nearClip, farClip);
                }
                if (renderAtmo)
                {
                    //atmo
                    renderCubeFace(debugCube, face, pos, atmosphereMask, nearClip, farClip);
                }
                if (renderScenery)
                {
                    //scene
                    eveCameraFix.overwriteAlpha = eveInstalled;
                    renderCubeFace(debugCube, face, pos, sceneryMask, nearClip, farClip);
                    eveCameraFix.overwriteAlpha = false;
                }
            }
            exportCubemap(debugCube, "reflect");
            reflectionCamera.enabled = false;
        }

        private void renderCubeFace(Cubemap envMap, CubemapFace face, Vector3 cameraPos, int layerMask, float nearClip, float farClip)
        {
            cameraSetup(cameraPos, layerMask, nearClip, farClip);
            int faceMask = 1 << (int)face;
            reflectionCamera.RenderToCubemap(envMap, faceMask);
        }

        private void renderCube(Cubemap envMap, Vector3 cameraPos, int layerMask, float nearClip, float farClip)
        {
            cameraSetup(cameraPos, layerMask, nearClip, farClip);
            reflectionCamera.RenderToCubemap(envMap);
        }

        private void exportCubemap(Cubemap envMap, string name)
        {
            Directory.CreateDirectory("cubeExport");
            Texture2D tex = new Texture2D(envMap.width, envMap.height, TextureFormat.ARGB32, false);
            for (int i = 0; i < 6; i++)
            {
                tex.SetPixels(envMap.GetPixels((CubemapFace)i));
                byte[] bytes = tex.EncodeToPNG();
                File.WriteAllBytes("cubeExport/" + name + "-" + i + ".png", bytes);
            }
            GameObject.Destroy(tex);
        }

        private void exportCubemap(RenderTexture envMap, string name)
        {
            Directory.CreateDirectory("cubeExport");
            Texture2D tex = new Texture2D(envMap.width, envMap.height, TextureFormat.ARGB32, false);
            for (int i = 0; i < 6; i++)
            {
                Graphics.SetRenderTarget(envMap, 0, (CubemapFace)i);
                tex.ReadPixels(new Rect(0, 0, envMap.width, envMap.height), 0, 0);
                tex.Apply();
                byte[] bytes = tex.EncodeToPNG();
                File.WriteAllBytes("cubeExport/" + name + "-" + i + ".png", bytes);
            }
            GameObject.Destroy(tex);
        }

        #endregion DEBUG RENDERING

        #region CONTAINER CLASSES

        public enum ReflectionPass
        {
            GALAXY,
            SCALED,
            SCENERY,
            ATMOSPHERE,
        }

        public class VesselReflectionData
        {
            public readonly ReflectionProbeData probeData;
            public VesselReflectionData(ReflectionProbeData data)
            {
                this.probeData = data;
            }
        }

        public class EditorReflectionData
        {
            public readonly ReflectionProbeData probeData;
            public EditorReflectionData(ReflectionProbeData data)
            {
                this.probeData = data;
            }
        }

        public class ReflectionProbeData
        {
            public readonly GameObject reflectionSphere;//also the owner of the probe
            public readonly ReflectionProbe probe;
            public readonly RenderTexture renderedCube;
            public readonly Material skyboxMateral;
            public readonly MeshRenderer render;
            public int updateFace = 0;
            public int updateTime = 0;
            public ReflectionProbeData(GameObject sphere, MeshRenderer rend, Material mat, ReflectionProbe probe, RenderTexture envMap)
            {
                this.reflectionSphere = sphere;
                this.render = rend;
                this.skyboxMateral = mat;
                this.probe = probe;
                this.renderedCube = envMap;
            }
        }

        //from unity post: https://forum.unity.com/threads/render-texture-alpha.2065/
        //potential fix to EVE writing 0 into alpha channel on areas subject to cloud textures
        //this should be somehow ran a single time -after- the last layer of a cube-side is rendered
        //had to move to a pre-compiled shader as apparently run-time compilation is completely unsupported now
        public class CameraAlphaFix : MonoBehaviour
        {
            private float alpha = 1.0f;
            private Material mat;
            public bool overwriteAlpha = true;
     
            public void Start()
            {
                Shader setAlpha = KSPShaderTools.KSPShaderLoader.getShader("SSTU/SetAlpha");
                mat = new Material(setAlpha);
            }

            public void OnPostRender()
            {
                if (overwriteAlpha)
                {
                    overwriteAlphaChannel();
                }
            }

            public void overwriteAlphaChannel()
            {
                GL.PushMatrix();
                GL.LoadOrtho();
                mat.SetFloat("_Alpha", alpha);
                mat.SetPass(0);
                GL.Begin(GL.QUADS);
                GL.Vertex3(0, 0, 0.5f);
                GL.Vertex3(1, 0, 0.5f);
                GL.Vertex3(1, 1, 0.5f);
                GL.Vertex3(0, 1, 0.5f);
                GL.End();
                GL.PopMatrix();
            }

        }

        #endregion

    }
}