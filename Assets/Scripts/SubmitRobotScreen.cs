using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The "Submit a Robot" screen: a player picks their FBX/URDF, says who they are, and sends it in.
//
// It is honest about what happens next — the robot is set up by hand in the Unity editor (collider
// generation, the drivetrain rig and every mechanism joint are Editor-only APIs, and the catalog
// holds direct prefab references) and comes back in an app update. Nothing here processes the file.
//
// Built and wired by Tools > RoboSim > Scenes > Build Home Screen, same as the other sub-screens.
public class SubmitRobotScreen : MonoBehaviour
{
    [Header("Panel")]
    [SerializeField] private GameObject panel;

    [Header("Destination")]
    [Tooltip("Where submissions go. Created empty by Build Home Screen; fill it in from the Firebase console.")]
    [SerializeField] private RobotUploadConfig config;

    [Header("Fields")]
    [SerializeField] private TMP_InputField teamInput;
    [SerializeField] private TMP_InputField robotInput;
    [SerializeField] private TMP_InputField contactInput;
    [SerializeField] private TMP_InputField notesInput;

    [Header("File")]
    [SerializeField] private Button chooseFileButton;
    [SerializeField] private TMP_Text fileLabel;

    [Header("Send")]
    [SerializeField] private Button sendButton;
    [SerializeField] private Slider progressBar;
    [SerializeField] private TMP_Text statusLabel;

    private string selectedPath;
    private bool sending;

    // Device path: the inbox is cycled through rather than shown as a list, because a player almost
    // always has exactly one file in there.
    private List<string> inbox = new List<string>();
    private int inboxIndex = -1;

    public void Open()
    {
        if (panel != null) panel.SetActive(true);
        sending = false;
        SetProgress(0f);
        RefreshFileLabel();
        RefreshSendButton();

        if (config == null || !config.IsConfigured)
        {
            SetStatus("Submitting isn't switched on in this build yet.");
        }
        else
        {
            SetStatus(config.turnaroundNote);
        }
    }

    public void Close()
    {
        if (panel != null) panel.SetActive(false);
    }

    // --- File choice ---

    public void OnChooseFilePressed()
    {
        if (sending) return;

        if (RobotFilePicker.CanBrowse)
        {
            string picked = RobotFilePicker.Browse();
            if (string.IsNullOrEmpty(picked)) return;
            Select(picked);
            return;
        }

        // No dialog on this platform: step through whatever the player copied into the app's folder.
        if (inbox.Count == 0 || inboxIndex < 0)
        {
            inbox = RobotFilePicker.InboxFiles();
            inboxIndex = -1;
        }
        if (inbox.Count == 0)
        {
            SetStatus("No robot files found. Copy your .fbx, .urdf or .zip into this app's folder " +
                      "using the Files app, then tap Choose File again.");
            return;
        }

        inboxIndex = (inboxIndex + 1) % inbox.Count;
        Select(inbox[inboxIndex]);
        if (inbox.Count > 1) SetStatus($"File {inboxIndex + 1} of {inbox.Count} — tap again for the next one.");
    }

    private void Select(string path)
    {
        if (!RobotFilePicker.LooksLikeRobotFile(path))
        {
            SetStatus("That isn't a robot file — send a .fbx, .urdf, or a .zip containing them.");
            return;
        }

        selectedPath = path;
        RefreshFileLabel();
        RefreshSendButton();

        // Guess a robot name from the file so most players never have to type one.
        if (robotInput != null && string.IsNullOrWhiteSpace(robotInput.text))
            robotInput.text = Path.GetFileNameWithoutExtension(path);
    }

    private void RefreshFileLabel()
    {
        if (fileLabel == null) return;

        if (string.IsNullOrEmpty(selectedPath))
        {
            fileLabel.text = RobotFilePicker.CanBrowse
                ? "No file chosen"
                : "No file chosen — copy one into this app's folder first";
            return;
        }

        long size = RobotFilePicker.SizeOf(selectedPath);
        fileLabel.text = $"{Path.GetFileName(selectedPath)}  ({RobotUploadService.Format(size)})";
    }

    // --- Sending ---

    public void OnSendPressed()
    {
        if (sending) return;

        if (string.IsNullOrEmpty(selectedPath))
        {
            SetStatus("Choose your robot file first.");
            return;
        }
        if (teamInput == null || string.IsNullOrWhiteSpace(teamInput.text))
        {
            SetStatus("Please put your team in — it's how your robot gets back to you.");
            return;
        }
        if (config == null || !config.IsConfigured)
        {
            SetStatus("Submitting isn't switched on in this build yet, so there's nowhere to send it.");
            return;
        }

        SetStatus("Reading your file…");
        byte[] bytes = RobotFilePicker.TryRead(selectedPath, out string readError);
        if (bytes == null)
        {
            SetStatus(readError);
            return;
        }

        RobotUploadService.Submission info = RobotUploadService.DescribeThisDevice(
            Text(teamInput), Text(robotInput), Text(contactInput), Text(notesInput),
            Path.GetFileName(selectedPath), bytes.LongLength,
            DateTime.UtcNow.ToString("o"));

        sending = true;
        RefreshSendButton();
        SetStatus("Sending…");
        StartCoroutine(RobotUploadService.Submit(config, info, bytes, SetProgress, OnSendFinished));
    }

    private void OnSendFinished(bool ok, string message)
    {
        sending = false;
        RefreshSendButton();
        SetProgress(ok ? 1f : 0f);

        if (!ok)
        {
            SetStatus(message);
            return;
        }

        SetStatus(message + " " + (config != null ? config.turnaroundNote : string.Empty));
        selectedPath = null;
        RefreshFileLabel();
        RefreshSendButton();
    }

    private void RefreshSendButton()
    {
        if (sendButton != null)
            sendButton.interactable = !sending && !string.IsNullOrEmpty(selectedPath);
        if (chooseFileButton != null) chooseFileButton.interactable = !sending;
    }

    private void SetProgress(float value)
    {
        if (progressBar == null) return;
        progressBar.gameObject.SetActive(value > 0f && value < 1f);
        progressBar.SetValueWithoutNotify(Mathf.Clamp01(value));
    }

    private void SetStatus(string message)
    {
        if (statusLabel != null) statusLabel.text = message;
    }

    private static string Text(TMP_InputField field)
    {
        return field != null && field.text != null ? field.text.Trim() : string.Empty;
    }
}
