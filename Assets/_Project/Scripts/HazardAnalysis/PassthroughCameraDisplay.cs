using UnityEngine;
using PassthroughCameraSamples;

/// <summary>
/// Debug/preview helper: shows a snapshot of the passthrough camera on a quad.
///
/// When the Touch controller's A button (OVRInput.Button.One) is pressed, it captures the
/// current camera frame, puts it on the quad's material, and places and scales the quad in
/// front of the left passthrough camera so it matches the camera's horizontal field of view.
/// Useful for checking what image will be sent to the model. Not part of the scan flow itself.
/// </summary>
public class PassthroughCameraDisplay : MonoBehaviour
{
    [Header("Inputs")]
    public WebCamTextureManager webcamManager;
    public Renderer quadRenderer;

    [Header("Quad Placement")]
    [Range(0.1f, 5f)]
    public float quadDistance = 1f;

    [Header("Material Property")]
    [Tooltip("Shader texture property. Built-in: _MainTex, URP Lit: _BaseMap.")]
    [SerializeField] private string textureName = "_MainTex";

    private Texture2D picture;

    void Start()
    {
        if (quadRenderer != null)
        {
            // Hide until we have an image to show
            quadRenderer.gameObject.SetActive(false);
        }
    }

    void Update()
    {
        if (webcamManager != null && webcamManager.WebCamTexture != null)
        {
            // A button on the Touch controller
            if (OVRInput.GetDown(OVRInput.Button.One))
            {
                TakePicture();
                PlaceQuad();
            }
        }
    }

    public void TakePicture()
    {
        if (quadRenderer == null || webcamManager == null || webcamManager.WebCamTexture == null)
            return;

        quadRenderer.gameObject.SetActive(true);

        var camTex = webcamManager.WebCamTexture;
        int width = camTex.width;
        int height = camTex.height;

        if (picture == null || picture.width != width || picture.height != height)
        {
            picture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        }

        var pixels = new Color32[width * height];
        camTex.GetPixels32(pixels);

        picture.SetPixels32(pixels);
        picture.Apply(false);

        // Instantiates a per-object material instance on first access
        quadRenderer.material.SetTexture(textureName, picture);
    }

    public void PlaceQuad()
    {
        if (quadRenderer == null) return;

        Transform quadTransform = quadRenderer.transform;

        // Get HMD pose and intrinsics for the left passthrough camera
        Pose cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(PassthroughCameraEye.Left);
        var intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(PassthroughCameraEye.Left);
        var resolution = intrinsics.Resolution; // has x,y

        // Position the quad directly in front of the camera at the chosen distance
        quadTransform.position = cameraPose.position + cameraPose.forward * quadDistance;
        quadTransform.rotation = cameraPose.rotation;

        // Two rays at mid-height: one at left edge (x=0), one at right edge (x=width-1)
        Ray leftSide  = PassthroughCameraUtils.ScreenPointToRayInCamera(
            PassthroughCameraEye.Left, new Vector2Int(0, resolution.y / 2));
        Ray rightSide = PassthroughCameraUtils.ScreenPointToRayInCamera(
            PassthroughCameraEye.Left, new Vector2Int(Mathf.Max(0, resolution.x - 1), resolution.y / 2));

        float horizontalFov = Vector3.Angle(leftSide.direction, rightSide.direction);
        float quadWidthAtDistance = 2f * quadDistance * Mathf.Tan((horizontalFov * Mathf.Deg2Rad) / 2f);

        // Guard in case TakePicture hasn't created the texture yet
        float aspect = (picture != null && picture.width > 0) ? (float)picture.height / picture.width : 9f / 16f;

        quadTransform.localScale = new Vector3(quadWidthAtDistance, quadWidthAtDistance * aspect, 1f);
    }
}
