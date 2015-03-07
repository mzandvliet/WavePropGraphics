using UnityEngine;
using UnityStandardAssets.ImageEffects;

[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
[AddComponentMenu("Image Effects/Rendering/Atmospheric Fog")]

public class AtmosphericFog : PostEffectsBase {
	private float CAMERA_NEAR = 0.5f;
	private float CAMERA_FAR = 50.0f;
	private float CAMERA_FOV = 60.0f;	
	private float CAMERA_ASPECT_RATIO = 1.333333f;

    [SerializeField] private Transform _sun;
    [SerializeField] private float globalDensity = 1.0f;
    [SerializeField] private float _seaLevel = 0f;
    [SerializeField] private float heightScale = 0.001f;
    [SerializeField] private float auraPower = 8f;

    [SerializeField] private Color fogColor = Color.grey;
    [SerializeField] private Color sunColor = Color.grey;
	
	public Shader fogShader;
	private Material fogMaterial = null;

	public override bool CheckResources() {
		CheckSupport (true);
	    
		fogMaterial = CheckShaderAndCreateMaterial (fogShader, fogMaterial);
		
		if(!isSupported)
			ReportAutoDisable ();

		return isSupported;				
	}

	private void OnRenderImage (RenderTexture source, RenderTexture destination) {
        if (CheckResources() == false) {
            Graphics.Blit(source, destination);
            return;
        }
			
		CAMERA_NEAR = GetComponent<Camera>().nearClipPlane;
		CAMERA_FAR = GetComponent<Camera>().farClipPlane;
		CAMERA_FOV = GetComponent<Camera>().fieldOfView;
		CAMERA_ASPECT_RATIO = GetComponent<Camera>().aspect;
	
		Matrix4x4 frustumCorners = Matrix4x4.identity;		
	
		float fovWHalf = CAMERA_FOV * 0.5f;
		
		Vector3 toRight = GetComponent<Camera>().transform.right * CAMERA_NEAR * Mathf.Tan (fovWHalf * Mathf.Deg2Rad) * CAMERA_ASPECT_RATIO;
		Vector3 toTop = GetComponent<Camera>().transform.up * CAMERA_NEAR * Mathf.Tan (fovWHalf * Mathf.Deg2Rad);
	
		Vector3 topLeft = (GetComponent<Camera>().transform.forward * CAMERA_NEAR - toRight + toTop);
		float CAMERA_SCALE = topLeft.magnitude * CAMERA_FAR/CAMERA_NEAR;

		topLeft.Normalize();
		topLeft *= CAMERA_SCALE;
	
		Vector3 topRight = (GetComponent<Camera>().transform.forward * CAMERA_NEAR + toRight + toTop);
		topRight.Normalize();
		topRight *= CAMERA_SCALE;
		
		Vector3 bottomRight = (GetComponent<Camera>().transform.forward * CAMERA_NEAR + toRight - toTop);
		bottomRight.Normalize();
		bottomRight *= CAMERA_SCALE;
		
		Vector3 bottomLeft = (GetComponent<Camera>().transform.forward * CAMERA_NEAR - toRight - toTop);
		bottomLeft.Normalize();
		bottomLeft *= CAMERA_SCALE;
				
		frustumCorners.SetRow (0, topLeft); 
		frustumCorners.SetRow (1, topRight);		
		frustumCorners.SetRow (2, bottomRight);
		frustumCorners.SetRow (3, bottomLeft);

	    fogMaterial.SetMatrix ("_FrustumCornersWS", frustumCorners);
		fogMaterial.SetVector ("_CameraWS", GetComponent<Camera>().transform.position);
		fogMaterial.SetVector ("_SunDir", -_sun.forward);
		
		fogMaterial.SetFloat ("_GlobalDensity", globalDensity);
        fogMaterial.SetFloat("_SeaLevel", _seaLevel);
        fogMaterial.SetFloat("_HeightScale", heightScale);
        fogMaterial.SetFloat("_AuraPower", auraPower);
		fogMaterial.SetColor ("_FogColor", fogColor);
        fogMaterial.SetColor("_SunColor", sunColor);

        //Graphics.Blit(source, destination, fogMaterial);
		CustomGraphicsBlit (source, destination, fogMaterial);
	}

    // Todo: What makes this different from Graphics.Blit? The effect breaks when using the latter.
    private static void CustomGraphicsBlit(RenderTexture source, RenderTexture dest, Material fxMaterial) {
        RenderTexture.active = dest;

        fxMaterial.SetTexture("_MainTex", source);

        GL.PushMatrix();
        GL.LoadOrtho();

        fxMaterial.SetPass (0);	

        GL.Begin(GL.QUADS);

        GL.MultiTexCoord2(0, 0.0f, 0.0f);
        GL.Vertex3(0.0f, 0.0f, 3.0f); // BL

        GL.MultiTexCoord2(0, 1.0f, 0.0f);
        GL.Vertex3(1.0f, 0.0f, 2.0f); // BR

        GL.MultiTexCoord2(0, 1.0f, 1.0f);
        GL.Vertex3(1.0f, 1.0f, 1.0f); // TR

        GL.MultiTexCoord2(0, 0.0f, 1.0f);
        GL.Vertex3(0.0f, 1.0f, 0.0f); // TL

        GL.End();
        GL.PopMatrix();
    }		
}
