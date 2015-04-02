using System;
using UnityEngine;
using System.Collections;

[ExecuteInEditMode]
public class UnityTerrain : MonoBehaviour {
    [SerializeField] private Texture2D _height0;
    [SerializeField] private Texture2D _height1;
    [SerializeField] private Texture2D _height2;
    [SerializeField] private Texture2D _height3;
    [SerializeField] private Texture2D _globalColor;
    [SerializeField] private Texture2D _globalNormal;

    public bool _initialized;

    // Update is called once per frame
    void Update() {
        if (!_initialized) {
            Terrain terrain = GetComponent<Terrain>();

            terrain.materialTemplate.SetTexture("_Height0", _height0);
            terrain.materialTemplate.SetTexture("_Height1", _height1);
            terrain.materialTemplate.SetTexture("_Height2", _height2);
            terrain.materialTemplate.SetTexture("_Height3", _height3);
            terrain.materialTemplate.SetTexture("_GlobalColorTex", _globalColor);
            terrain.materialTemplate.SetTexture("_GlobalNormalTex", _globalNormal);

            _initialized = true;
        }
    }
}
