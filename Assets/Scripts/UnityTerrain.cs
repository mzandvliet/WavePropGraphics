using System;
using UnityEngine;
using System.Collections;

[ExecuteInEditMode]
public class UnityTerrain : MonoBehaviour {
    [SerializeField] private Texture2D _globalColor;
    [SerializeField] private Texture2D _globalNormal;

    public bool _initialized;

    // Update is called once per frame
    void Update() {
        if (!_initialized) {
            Terrain terrain = GetComponent<Terrain>();
            terrain.materialTemplate.SetTexture("_GlobalColorTex", _globalColor);
            terrain.materialTemplate.SetTexture("_GlobalNormalTex", _globalNormal);
            _initialized = true;
        }
    }
}
