using TMPro;
using UnityEngine;

public class FPSDisplay : MonoBehaviour
{
	[Header("UI")]
	[SerializeField] private TextMeshProUGUI fpsLabel;

	[Header("Update cadence")]
	[SerializeField] private float updateIntervalSeconds = 0.5f;

	[Header("Coloring")]
	[SerializeField] private bool useColor = true;
	[SerializeField] private int targetFps = 60;
	[SerializeField] private Color goodColor = new Color(0.2f, 0.85f, 0.2f);
	[SerializeField] private Color okayColor = new Color(0.95f, 0.8f, 0.2f);
	[SerializeField] private Color badColor = new Color(0.95f, 0.35f, 0.35f);

	private float accumulatedTime;
	private int accumulatedFrames;
	private float currentFps;

	private void Reset()
	{
		if (fpsLabel == null)
		{
			TryGetComponent(out fpsLabel);
		}
	}

	private void Awake()
	{
		if (fpsLabel == null)
		{
			TryGetComponent(out fpsLabel);
		}
	}

	private void Update()
	{
		// Use unscaled time so pausing Time.timeScale doesn't freeze the counter.
		accumulatedTime += Time.unscaledDeltaTime;
		accumulatedFrames++;

		if (accumulatedTime >= Mathf.Max(0.05f, updateIntervalSeconds))
		{
			currentFps = accumulatedFrames / accumulatedTime;
			UpdateLabel(currentFps);

			accumulatedTime = 0f;
			accumulatedFrames = 0;
		}
	}

	private void UpdateLabel(float fps)
	{
		if (fpsLabel == null) return;

		float clampedFps = Mathf.Max(0.0001f, fps);
		float ms = 1000f / clampedFps;

		fpsLabel.text = $"FPS: {clampedFps:0}  ({ms:0.0} ms)";

		if (!useColor) return;

		if (targetFps > 0)
		{
			float ratio = clampedFps / targetFps;
			if (ratio >= 0.9f)
				fpsLabel.color = goodColor;
			else if (ratio >= 0.6f)
				fpsLabel.color = okayColor;
			else
				fpsLabel.color = badColor;
		}
		else
		{
			// Fallback thresholds when no target is set
			if (clampedFps >= 50f)
				fpsLabel.color = goodColor;
			else if (clampedFps >= 30f)
				fpsLabel.color = okayColor;
			else
				fpsLabel.color = badColor;
		}
	}
}