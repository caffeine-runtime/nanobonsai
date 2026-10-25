#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public static class CreateCefTemplateScene
{
    [MenuItem("CEF/Create Nanobonsai Sample Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        GameObject canvasObject = new GameObject("Canvas");
        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObject.AddComponent<CanvasScaler>();
        canvasObject.AddComponent<GraphicRaycaster>();

        GameObject panelObject = new GameObject("HtmlUiPanel");
        panelObject.transform.SetParent(canvasObject.transform, false);

        RawImage rawImage = panelObject.AddComponent<RawImage>();
        RectTransform rectTransform = rawImage.GetComponent<RectTransform>();
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;

        HtmlUiView view = panelObject.AddComponent<HtmlUiView>();
        view.matchScreenSize = true;
        view.width = 1280;
        view.height = 720;

        panelObject.AddComponent<CefTemplateBridge>();

        EnsureEventSystem();

        Directory.CreateDirectory("Assets/Scenes");
        const string scenePath = "Assets/Scenes/CefTemplateScene.unity";
        if (EditorSceneManager.SaveScene(scene, scenePath))
        {
            Debug.Log("CEF template scene created at: " + scenePath);
            SceneAsset sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
            ProjectWindowUtil.ShowCreatedAsset(sceneAsset);
        }
        else
        {
            Debug.LogError("Failed to save CEF template scene.");
        }
    }

    private static void EnsureEventSystem()
    {
        if (Object.FindFirstObjectByType<EventSystem>() != null)
        {
            return;
        }

        GameObject eventSystemObject = new GameObject("EventSystem");
        eventSystemObject.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
        eventSystemObject.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
        eventSystemObject.AddComponent<StandaloneInputModule>();
#endif
    }
}
#endif
