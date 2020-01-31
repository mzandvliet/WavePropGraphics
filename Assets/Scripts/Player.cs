using UnityEngine;
using System.Collections;

public class Player : MonoBehaviour {
    [SerializeField] private Camera _camera;
    [SerializeField] private float _flySpeed = 400f;

    private void Awake() {
        if (!_camera) {
            _camera = gameObject.GetComponentInChildren<Camera>();
        }
    }

	// Update is called once per frame
	void Update () {
        float inputHorizontal = Input.GetAxis("Horizontal");
        float inputVertical = Input.GetAxis("Vertical");

        float inputX = Input.GetAxis("Mouse X");
        float inputY = Input.GetAxis("Mouse Y");

        transform.Rotate(0f, inputX * 120f * Time.deltaTime, 0f, Space.World);
        transform.Rotate(inputY * -120f * Time.deltaTime, 0f, 0f, Space.Self);
	    transform.Translate(
            inputHorizontal * _flySpeed * Time.deltaTime, 
            0f,
            inputVertical * _flySpeed * Time.deltaTime, Space.Self);

	    if (Input.GetKeyDown(KeyCode.T)) {
	        _wireframe = !_wireframe;
	    }
	}

    private bool _wireframe;

    private void OnPreRender() {
        UpdateWireframeMode();
    }

    private void OnPostRender() {
        UpdateWireframeMode();
    }
    
    private void UpdateWireframeMode() {
        _camera.clearFlags = _wireframe ? CameraClearFlags.Color : CameraClearFlags.Skybox;
        GL.wireframe = _wireframe;
    }
}
