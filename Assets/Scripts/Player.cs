using UnityEngine;
using System.Collections;

public class Player : MonoBehaviour {

	// Use this for initialization
	void Start () {
	
	}
	
	// Update is called once per frame
	void Update () {
        float inputHorizontal = Input.GetAxis("Horizontal");
        float inputVertical = Input.GetAxis("Vertical");

        float inputX = Input.GetAxis("Mouse X");
        float inputY = Input.GetAxis("Mouse Y");

        transform.Rotate(0f, inputX * 120f * Time.deltaTime, 0f, Space.World);
        transform.Rotate(inputY * -120f * Time.deltaTime, 0f, 0f, Space.Self);
	    transform.Translate(inputHorizontal * 200f * Time.deltaTime, 0f, inputVertical * 200f * Time.deltaTime, Space.Self);
	}
}
