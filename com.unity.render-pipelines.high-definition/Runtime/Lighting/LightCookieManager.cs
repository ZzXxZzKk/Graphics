using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>
    /// Available graphic formats for the cookie atlas texture.
    /// </summary>
    [System.Serializable]
    public enum CookieAtlasGraphicsFormat
    {
        /// <summary>Faster sampling but at the cost of precision.</summary>
        R11G11B10 = GraphicsFormat.B10G11R11_UFloatPack32,
        /// <summary>Better quality and more precision for HDR cookies but uses twice as much memory compared to R11G11B10.</summary>
        R16G16B16A16 = GraphicsFormat.R16G16B16A16_SFloat,
    }

    class LightCookieManager
    {
        HDRenderPipelineAsset m_RenderPipelineAsset = null;

        internal static readonly int s_texSource = Shader.PropertyToID("_SourceTexture");
        internal static readonly int s_sourceMipLevel = Shader.PropertyToID("_SourceMipLevel");
        internal static readonly int s_sourceSize = Shader.PropertyToID("_SourceSize");
        internal static readonly int s_uvLimits = Shader.PropertyToID("_UVLimits");

        internal const int k_MinCookieSize = 2;

        readonly Material m_MaterialFilterAreaLights;
        MaterialPropertyBlock m_MPBFilterAreaLights = new MaterialPropertyBlock();

        readonly ComputeShader m_ProjectCubeTo2D;
        readonly int m_KernalFastOctahedral;

        RenderTexture m_TempRenderTexture0 = null;
        RenderTexture m_TempRenderTexture1 = null;

        // Structure for cookies used by directional and spotlights
        PowerOfTwoTextureAtlas m_CookieAtlas;

        int m_CookieCubeResolution;

        // During the light loop, when reserving space for the cookies (first part of the light loop) the atlas
        // can run out of space, in this case, we set to true this flag which will trigger a re-layouting of the
        // atlas (sort entries by size and insert them again).
        bool                m_2DCookieAtlasNeedsLayouting = false;
        bool                m_NoMoreSpace = false;
        readonly int        cookieAtlasLastValidMip;
        readonly GraphicsFormat cookieFormat;

        private Dictionary<int, RTHandle> m_AreaLightTextureEmissive = new Dictionary<int, RTHandle>();
        public LightCookieManager(HDRenderPipelineAsset hdAsset, int maxCacheSize)
        {
            // Keep track of the render pipeline asset
            m_RenderPipelineAsset = hdAsset;
            var hdResources = HDRenderPipeline.defaultAsset.renderPipelineResources;

            // Create the texture cookie cache that we shall be using for the area lights
            GlobalLightLoopSettings gLightLoopSettings = hdAsset.currentPlatformRenderPipelineSettings.lightLoopSettings;

            // Also make sure to create the engine material that is used for the filtering
            m_MaterialFilterAreaLights = CoreUtils.CreateEngineMaterial(hdResources.shaders.filterAreaLightCookiesPS);

            m_ProjectCubeTo2D = hdResources.shaders.projectCubeTo2DCS;

            m_KernalFastOctahedral = m_ProjectCubeTo2D.FindKernel("CSMainFastOctahedral");

            int cookieAtlasSize = (int)gLightLoopSettings.cookieAtlasSize;
            cookieFormat = (GraphicsFormat)gLightLoopSettings.cookieFormat;
            cookieAtlasLastValidMip = gLightLoopSettings.cookieAtlasLastValidMip;

            if (PowerOfTwoTextureAtlas.GetApproxCacheSizeInByte(1, cookieAtlasSize, true, cookieFormat) > HDRenderPipeline.k_MaxCacheSize)
                cookieAtlasSize = PowerOfTwoTextureAtlas.GetMaxCacheSizeForWeightInByte(HDRenderPipeline.k_MaxCacheSize, true, cookieFormat);

            m_CookieAtlas = new PowerOfTwoTextureAtlas(cookieAtlasSize, gLightLoopSettings.cookieAtlasLastValidMip, cookieFormat, name: "Cookie Atlas (Punctual Lights)", useMipMap: true);

            m_CookieCubeResolution = (int)gLightLoopSettings.pointCookieSize;
        }

        public void NewFrame()
        {
            m_CookieAtlas.ResetRequestedTexture();
            m_2DCookieAtlasNeedsLayouting = false;
            m_NoMoreSpace = false;
        }

        public void Release()
        {
            CoreUtils.Destroy(m_MaterialFilterAreaLights);

            if(m_TempRenderTexture0 != null)
            {
                m_TempRenderTexture0.Release();
                m_TempRenderTexture0 = null;
            }
            if (m_TempRenderTexture1 != null)
            {
                m_TempRenderTexture1.Release();
                m_TempRenderTexture1 = null;
            }

            if (m_CookieAtlas != null)
            {
                m_CookieAtlas.Release();
                m_CookieAtlas = null;
            }

            if (m_AreaLightTextureEmissive != null)
            {
                foreach (var cur in m_AreaLightTextureEmissive)
                {
                    RTHandles.Release(cur.Value);
                }
                m_AreaLightTextureEmissive = null;
            }
        }

        Texture FilterAreaLightTexture(CommandBuffer cmd, Texture source)
        {
            if (m_MaterialFilterAreaLights == null)
            {
                Debug.LogError("FilterAreaLightTexture has an invalid shader. Can't filter area light cookie.");
                return null;
            }

            // TODO: we don't need to allocate two temp RT, we can use the atlas as temp render texture
            // it will avoid additional copy of the whole mip chain into the atlas.
            int sourceWidth = m_CookieAtlas.AtlasTexture.rt.width;
            int sourceHeight = m_CookieAtlas.AtlasTexture.rt.height;
            int viewportWidth = source.width;
            int viewportHeight = source.height;
            int mipMapCount = 1 + Mathf.FloorToInt(Mathf.Log(Mathf.Max(source.width, source.height), 2));

            if (m_TempRenderTexture0 == null)
            {
                string cacheName = m_CookieAtlas.AtlasTexture.name;
                m_TempRenderTexture0 = new RenderTexture(sourceWidth, sourceHeight, 1, cookieFormat)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = true,
                    autoGenerateMips = false,
                    name = cacheName + "TempAreaLightRT0"
                };

                // We start by a horizontal gaussian into mip 1 that reduces the width by a factor 2 but keeps the same height
                m_TempRenderTexture1 = new RenderTexture(sourceWidth >> 1, sourceHeight, 1, cookieFormat)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = true,
                    autoGenerateMips = false,
                    name = cacheName + "TempAreaLightRT1"
                };
            }

            using (new ProfilingScope(cmd, ProfilingSampler.Get(HDProfileId.AreaLightCookieConvolution)))
            {
                int targetWidth = sourceWidth;
                int targetHeight = sourceHeight;

                // Start by copying the source texture to the array slice's mip 0
                {
                    m_MPBFilterAreaLights.SetInt(s_sourceMipLevel, 0);
                    m_MPBFilterAreaLights.SetTexture(s_texSource, source);

                    cmd.SetRenderTarget(m_TempRenderTexture0, 0);
                    cmd.SetViewport(new Rect(0, 0, viewportWidth, viewportHeight));
                    cmd.DrawProcedural(Matrix4x4.identity, m_MaterialFilterAreaLights, 0, MeshTopology.Triangles, 3, 1, m_MPBFilterAreaLights);
                }

                // Then operate on all the remaining mip levels
                Vector4 sourceSize = Vector4.zero;
                for (int mipIndex=1; mipIndex < mipMapCount; mipIndex++)
                {
                    {   // Perform horizontal blur
                        sourceSize.Set(viewportWidth / (float)sourceWidth * 1.0f, viewportHeight / (float)sourceHeight, 1.0f / sourceWidth, 1.0f / sourceHeight);
                        Vector4 uvLimits = new Vector4(0, 0, viewportWidth / (float)sourceWidth, viewportHeight / (float)sourceHeight);

                        viewportWidth = Mathf.Max(1, viewportWidth >> 1);
                        targetWidth = Mathf.Max(1, targetWidth  >> 1);

                        m_MPBFilterAreaLights.SetTexture(s_texSource, m_TempRenderTexture0);
                        m_MPBFilterAreaLights.SetInt(s_sourceMipLevel, mipIndex - 1);
                        m_MPBFilterAreaLights.SetVector(s_sourceSize, sourceSize);
                        m_MPBFilterAreaLights.SetVector(s_uvLimits, uvLimits);

                        cmd.SetRenderTarget(m_TempRenderTexture1, mipIndex-1);    // Temp texture is already 1 mip lower than source
                        cmd.SetViewport(new Rect(0, 0, viewportWidth, viewportHeight));
                        cmd.DrawProcedural(Matrix4x4.identity, m_MaterialFilterAreaLights, 1, MeshTopology.Triangles, 3, 1, m_MPBFilterAreaLights);
                    }

                    sourceWidth = targetWidth;

                    {   // Perform vertical blur
                        sourceSize.Set(viewportWidth / (float)sourceWidth, viewportHeight / (float)sourceHeight * 1.0f, 1.0f / sourceWidth, 1.0f / sourceHeight);
                        Vector4 uvLimits = new Vector4(0, 0, viewportWidth / (float)sourceWidth, viewportHeight / (float)sourceHeight);

                        viewportHeight = Mathf.Max(1, viewportHeight >> 1);
                        targetHeight = Mathf.Max(1, targetHeight >> 1);

                        m_MPBFilterAreaLights.SetTexture(s_texSource, m_TempRenderTexture1);
                        m_MPBFilterAreaLights.SetInt(s_sourceMipLevel, mipIndex - 1);
                        m_MPBFilterAreaLights.SetVector(s_sourceSize, sourceSize);
                        m_MPBFilterAreaLights.SetVector(s_uvLimits, uvLimits);

                        cmd.SetRenderTarget(m_TempRenderTexture0, mipIndex);
                        cmd.SetViewport(new Rect(0, 0, viewportWidth, viewportHeight));
                        cmd.DrawProcedural(Matrix4x4.identity, m_MaterialFilterAreaLights, 2, MeshTopology.Triangles, 3, 1, m_MPBFilterAreaLights);
                    }

                    sourceHeight = targetHeight;
                }
            }

            return m_TempRenderTexture0;
        }

        public void LayoutIfNeeded()
        {
            if (!m_2DCookieAtlasNeedsLayouting)
                return;

            if (!m_CookieAtlas.RelayoutEntries())
            {
                Debug.LogError($"No more space in the 2D Cookie Texture Atlas. To solve this issue, increase the resolution of the cookie atlas in the HDRP settings.");
                m_NoMoreSpace = true;
            }
        }

        public Vector4 Fetch2DCookie(CommandBuffer cmd, Texture cookie, Texture ies)
        {
            int width  = (int)Mathf.Max(cookie.width, ies.height);
            int height = (int)Mathf.Max(cookie.width, ies.height);

            if (width < k_MinCookieSize || height < k_MinCookieSize)
                return Vector4.zero;

            if (!m_CookieAtlas.IsCached(out var scaleBias, m_CookieAtlas.GetTextureID(cookie, ies)) && !m_NoMoreSpace)
                Debug.LogError($"2D Light cookie texture {cookie} can't be fetched without having reserved. You can try to increase the cookie atlas resolution in the HDRP settings.");

            if (m_CookieAtlas.NeedsUpdate(cookie, ies, false))
            {
                RTHandle multiplied = RTHandles.Alloc(  width, height,
                                                        colorFormat: cookieFormat,
                                                        enableRandomWrite: true);
                RTHandle tex0;
                RTHandle tex1;
                if (cookie.width*cookie.height > ies.width*ies.height)
                {
                    tex0 = RTHandles.Alloc(cookie);
                    tex1 = RTHandles.Alloc(ies);
                }
                else
                {
                    tex0 = RTHandles.Alloc(ies);
                    tex1 = RTHandles.Alloc(cookie);
                }
                cmd.SetRenderTarget(multiplied);
                cmd.ClearRenderTarget(false, true, Color.black);
                GPUArithmetic.ComputeOperation(multiplied, tex0, cmd, GPUArithmetic.Operation.Add);
                GPUArithmetic.ComputeOperation(multiplied, tex1, cmd, GPUArithmetic.Operation.Mult);

                m_CookieAtlas.BlitTexture(cmd, scaleBias, multiplied, new Vector4(1, 1, 0, 0), blitMips: false, overrideInstanceID: m_CookieAtlas.GetTextureID(cookie, ies));

                RTHandlesDeleter.ScheduleRelease(tex0);
                RTHandlesDeleter.ScheduleRelease(tex1);
                RTHandlesDeleter.ScheduleRelease(multiplied);
            }

            return scaleBias;
        }

        public Vector4 Fetch2DCookie(CommandBuffer cmd, Texture cookie)
        {
            if (cookie.width < k_MinCookieSize || cookie.height < k_MinCookieSize)
                return Vector4.zero;

            if (!m_CookieAtlas.IsCached(out var scaleBias, m_CookieAtlas.GetTextureID(cookie)) && !m_NoMoreSpace)
                Debug.LogError($"2D Light cookie texture {cookie} can't be fetched without having reserved. You can try to increase the cookie atlas resolution in the HDRP settings.");

            if (m_CookieAtlas.NeedsUpdate(cookie, false))
                m_CookieAtlas.BlitTexture(cmd, scaleBias, cookie, new Vector4(1, 1, 0, 0), blitMips: false);

            return scaleBias;
        }

        public Vector4 FetchAreaCookie(CommandBuffer cmd, Texture cookie, Texture ies, ref Texture emissive)
        {
            int width  = (int)Mathf.Max(cookie.width, ies.height);
            int height = (int)Mathf.Max(cookie.width, ies.height);

            if (width < k_MinCookieSize || height < k_MinCookieSize)
                return Vector4.zero;

            if (!m_CookieAtlas.IsCached(out var scaleBias, cookie, ies) && !m_NoMoreSpace)
                Debug.LogError($"Area Light cookie texture {cookie} & {ies} can't be fetched without having reserved. You can try to increase the cookie atlas resolution in the HDRP settings.");

            int currentID = m_CookieAtlas.GetTextureID(cookie, ies);
            if (m_CookieAtlas.NeedsUpdate(cookie, ies, true))
            {
                RTHandle multiplied = RTHandles.Alloc(  width, height,
                                                        colorFormat: cookieFormat,
                                                        enableRandomWrite: true);

                // TODO: we don't need to allocate two temp RT, we can use the atlas as temp render texture
                // it will avoid additional copy of the whole mip chain into the atlas.
                RTHandle tex0;
                RTHandle tex1;
                if (cookie.width*cookie.height > ies.width*ies.height)
                {
                    tex0 = RTHandles.Alloc(cookie);
                    tex1 = RTHandles.Alloc(ies);
                }
                else
                {
                    tex0 = RTHandles.Alloc(ies);
                    tex1 = RTHandles.Alloc(cookie);
                }
                cmd.SetRenderTarget(multiplied);
                cmd.ClearRenderTarget(false, true, Color.black);
                GPUArithmetic.ComputeOperation(multiplied, tex0, cmd, GPUArithmetic.Operation.Add);
                GPUArithmetic.ComputeOperation(multiplied, tex1, cmd, GPUArithmetic.Operation.Mult);

                // Generate the mips
                Texture filteredAreaLight = FilterAreaLightTexture(cmd, multiplied);
                Vector4 sourceScaleOffset = new Vector4(multiplied.rt.width/(float)atlasTexture.rt.width, multiplied.rt.height/(float)atlasTexture.rt.height, 0, 0);
                m_CookieAtlas.BlitTexture(cmd, scaleBias, filteredAreaLight, sourceScaleOffset, blitMips: true, overrideInstanceID: currentID);

                RTHandlesDeleter.ScheduleRelease(tex0);
                RTHandlesDeleter.ScheduleRelease(tex1);

                RTHandle existingTexture;
                if (m_AreaLightTextureEmissive.TryGetValue(currentID, out existingTexture))
                {
                    RTHandlesDeleter.ScheduleRelease(existingTexture);
                }
                m_AreaLightTextureEmissive[currentID] = multiplied;
            }

            emissive = m_AreaLightTextureEmissive[currentID];

            return scaleBias;
        }

        public Vector4 FetchAreaCookie(CommandBuffer cmd, Texture cookie, ref Texture emissive)
        {
            if (cookie.width < k_MinCookieSize || cookie.height < k_MinCookieSize)
                return Vector4.zero;

            if (!m_CookieAtlas.IsCached(out var scaleBias, cookie) && !m_NoMoreSpace)
                Debug.LogError($"Area Light cookie texture {cookie} can't be fetched without having reserved. You can try to increase the cookie atlas resolution in the HDRP settings.");

            int currentID = m_CookieAtlas.GetTextureID(cookie);
            RTHandle existingTexture;
            if (m_CookieAtlas.NeedsUpdate(cookie, true))
            {
                // Generate the mips
                Texture filteredAreaLight = FilterAreaLightTexture(cmd, cookie);
                Vector4 sourceScaleOffset = new Vector4(cookie.width / (float)atlasTexture.rt.width, cookie.height / (float)atlasTexture.rt.height, 0, 0);
                m_CookieAtlas.BlitTexture(cmd, scaleBias, filteredAreaLight, sourceScaleOffset, blitMips: true, overrideInstanceID: currentID);

                if (m_AreaLightTextureEmissive.TryGetValue(currentID, out existingTexture))
                {
                    RTHandlesDeleter.ScheduleRelease(existingTexture);
                }
                RTHandle stored = RTHandles.Alloc(  cookie.width, cookie.height,
                                                    autoGenerateMips: false,
                                                    useMipMap: true,
                                                    colorFormat: cookieFormat,
                                                    enableRandomWrite: true);
                int mipMapCount = stored.rt.mipmapCount;// 1 + Mathf.FloorToInt(Mathf.Log(Mathf.Max(cookie.width, cookie.height), 2));
                for (int k = 0; k < mipMapCount; ++k)
                {
                    cmd.CopyTexture(filteredAreaLight, 0, k, 0, 0, (int)(sourceScaleOffset.x*atlasTexture.rt.width), (int)(sourceScaleOffset.y*atlasTexture.rt.height),
                                    stored, 0, k, 0, 0);
                }
                m_AreaLightTextureEmissive[currentID] = stored;
            }

            emissive = m_AreaLightTextureEmissive[currentID];

            return scaleBias;
        }

        public void ReserveSpace(Texture cookieA, Texture cookieB)
        {
            if (cookieA == null || cookieB == null)
                return;

            int width  = (int)Mathf.Max(cookieA.width, cookieB.height);
            int height = (int)Mathf.Max(cookieA.width, cookieB.height);

            if (width < k_MinCookieSize || height < k_MinCookieSize)
                return;

            if (!m_CookieAtlas.ReserveSpace(m_CookieAtlas.GetTextureID(cookieA, cookieB), width, height))
                m_2DCookieAtlasNeedsLayouting = true;
        }

        public void ReserveSpace(Texture cookie)
        {
            if (cookie == null)
                return;

            if (cookie.width < k_MinCookieSize || cookie.height < k_MinCookieSize)
                return;

            if (!m_CookieAtlas.ReserveSpace(cookie))
                m_2DCookieAtlasNeedsLayouting = true;
        }

        public void ReserveSpaceCube(Texture cookie)
        {
            if (cookie == null)
                return;

            Debug.Assert(cookie.dimension == TextureDimension.Cube);

            int projectionSize  = 2*cookie.width;

            if (projectionSize < k_MinCookieSize)
                return;

            if (!m_CookieAtlas.ReserveSpace(m_CookieAtlas.GetTextureID(cookie), projectionSize, projectionSize))
                m_2DCookieAtlasNeedsLayouting = true;
        }

        public void ReserveSpaceCube(Texture cookieA, Texture cookieB)
        {
            if (cookieA == null && cookieB == null)
                return;

            Debug.Assert(cookieA.dimension == TextureDimension.Cube && cookieB.dimension == TextureDimension.Cube);

            int projectionSize  = 2*(int)Mathf.Max(cookieA.width, cookieB.width);

            if (projectionSize < k_MinCookieSize)
                return;

            if (!m_CookieAtlas.ReserveSpace(m_CookieAtlas.GetTextureID(cookieA, cookieB), projectionSize, projectionSize))
                m_2DCookieAtlasNeedsLayouting = true;
        }

        private RTHandle ProjectCubeTo2D(CommandBuffer cmd, Texture cookie, int projectionSize)
        {
            Debug.Assert(cookie.dimension == TextureDimension.Cube);

            RTHandle projected = RTHandles.Alloc(
                                        projectionSize,
                                        projectionSize,
                                        colorFormat: cookieFormat,
                                        enableRandomWrite: true);
            int usedKernel = m_KernalFastOctahedral;

            float invSize = 1.0f/((float)projectionSize);

            cmd.SetComputeTextureParam(m_ProjectCubeTo2D, usedKernel, HDShaderIDs._Input,  cookie);
            cmd.SetComputeTextureParam(m_ProjectCubeTo2D, usedKernel, HDShaderIDs._Output, projected);
            cmd.SetComputeFloatParams (m_ProjectCubeTo2D,             HDShaderIDs._ScaleBias,
                                        invSize, invSize, 0.5f*invSize, 0.5f*invSize);

            const int groupSizeX = 8;
            const int groupSizeY = 8;
            int threadGroupX = (projectionSize + (groupSizeX - 1))/groupSizeX;
            int threadGroupY = (projectionSize + (groupSizeY - 1))/groupSizeY;

            cmd.DispatchCompute(m_ProjectCubeTo2D, usedKernel, threadGroupX, threadGroupY, 1);

            return projected;
        }

        public Vector4 FetchCubeCookie(CommandBuffer cmd, Texture cookie)
        {
            Debug.Assert(cookie != null);
            Debug.Assert(cookie.dimension == TextureDimension.Cube);

            int projectionSize = 2*(int)Mathf.Max((float)m_CookieCubeResolution, (float)cookie.width);
            if (projectionSize < k_MinCookieSize)
                return Vector4.zero;

            if (!m_CookieAtlas.IsCached(out var scaleBias, cookie) && !m_NoMoreSpace)
                Debug.LogError($"Cube cookie texture {cookie} can't be fetched without having reserved. You can try to increase the cookie atlas resolution in the HDRP settings.");

            if (m_CookieAtlas.NeedsUpdate(cookie, false))
            {
                Vector4 sourceScaleOffset = new Vector4(projectionSize/(float)atlasTexture.rt.width, projectionSize/(float)atlasTexture.rt.height, 0, 0);

                RTHandle projectedCookie = ProjectCubeTo2D(cmd, cookie, projectionSize);

                // Generate the mips
                Texture filteredProjected = FilterAreaLightTexture(cmd, projectedCookie);
                m_CookieAtlas.BlitOctahedralTexture(cmd, scaleBias, filteredProjected, sourceScaleOffset, blitMips: true, overrideInstanceID: m_CookieAtlas.GetTextureID(cookie));

                RTHandlesDeleter.ScheduleRelease(projectedCookie);
            }

            return scaleBias;
        }

        public Vector4 FetchCubeCookie(CommandBuffer cmd, Texture cookie, Texture ies)
        {
            Debug.Assert(cookie != null);
            Debug.Assert(ies != null);
            Debug.Assert(cookie.dimension == TextureDimension.Cube);
            Debug.Assert(ies.dimension == TextureDimension.Cube);

            int projectionSize = 2*(int)Mathf.Max((float)m_CookieCubeResolution, (float)cookie.width);
            if (projectionSize < k_MinCookieSize)
                return Vector4.zero;

            if (!m_CookieAtlas.IsCached(out var scaleBias, cookie, ies) && !m_NoMoreSpace)
                Debug.LogError($"Cube cookie texture {cookie} can't be fetched without having reserved. You can try to increase the cookie atlas resolution in the HDRP settings.");

            if (m_CookieAtlas.NeedsUpdate(cookie, ies, true))
            {
                Vector4 sourceScaleOffset = new Vector4(projectionSize/(float)atlasTexture.rt.width, projectionSize/(float)atlasTexture.rt.height, 0, 0);

                RTHandle projectedCookie = ProjectCubeTo2D(cmd, cookie, projectionSize);
                RTHandle projectedIES    = ProjectCubeTo2D(cmd, ies,    projectionSize);

                GPUArithmetic.ComputeOperation(projectedCookie, projectedIES, cmd, GPUArithmetic.Operation.Mult);

                // Generate the mips
                Texture filteredProjected = FilterAreaLightTexture(cmd, projectedCookie);
                m_CookieAtlas.BlitOctahedralTexture(cmd, scaleBias, filteredProjected, sourceScaleOffset, blitMips: true, overrideInstanceID: m_CookieAtlas.GetTextureID(cookie, ies));

                RTHandlesDeleter.ScheduleRelease(projectedCookie, 64);
                RTHandlesDeleter.ScheduleRelease(projectedIES,    64);
            }

            return scaleBias;
        }

        public void ResetAllocator() => m_CookieAtlas.ResetAllocator();

        public void ClearAtlasTexture(CommandBuffer cmd) => m_CookieAtlas.ClearTarget(cmd);

        public RTHandle atlasTexture => m_CookieAtlas.AtlasTexture;

        public PowerOfTwoTextureAtlas atlas => m_CookieAtlas;

        public Vector4 GetCookieAtlasSize()
        {
            return new Vector4(
                m_CookieAtlas.AtlasTexture.rt.width,
                m_CookieAtlas.AtlasTexture.rt.height,
                1.0f / m_CookieAtlas.AtlasTexture.rt.width,
                1.0f / m_CookieAtlas.AtlasTexture.rt.height
           );
        }

        public Vector4 GetCookieAtlasDatas()
        {
            float padding = Mathf.Pow(2.0f, m_CookieAtlas.mipPadding) * 2.0f;
            return new Vector4(
                m_CookieAtlas.AtlasTexture.rt.width,
                padding / (float)m_CookieAtlas.AtlasTexture.rt.width,
                cookieAtlasLastValidMip,
                0
           );
        }
    }
}
