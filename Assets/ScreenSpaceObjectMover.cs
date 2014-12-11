using UnityEngine;

public class ScreenSpaceObjectMover : MonoBehaviour {
    [SerializeField] private Transform _selected;

	void Start () {
	
	}
	
	void Update () {
	    float inputX = Input.GetAxis("Horizontal");
        float inputY = Input.GetAxis("Vertical");

	    Quaternion localRotation = Quaternion.Euler(inputX * 20f * Time.deltaTime, 0f, inputY * 20f * Time.deltaTime);

	    Quaternion selectedLocal = Quaternion.Inverse(transform.rotation) * _selected.rotation;
	    selectedLocal = selectedLocal*localRotation;
	    _selected.rotation = transform.rotation*selectedLocal;
	}
}
