using UnityEngine;

[ExecuteInEditMode]
public class Spin : MonoBehaviour {

    void Update() {
        transform.Rotate(0f, 1f, 0f, Space.World);
    }
}
