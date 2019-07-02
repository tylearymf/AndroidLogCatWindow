using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class AndroidLogCatWindow : EditorWindow
{
    #region struct
    class Info
    {
        public string msg { set; private get; }
        public MessageType type { set; get; }
        public string trace { set; private get; }
        public int index { private set; get; }
        public int id { set; get; }

        string mTrace;
        string mMsg;
        public const int cItemHeight = 30;

        public string GetTrace()
        {
            if (string.IsNullOrEmpty(mTrace))
            {
                mTrace = msg + "\n" + trace;
            }
            return mTrace;
        }

        public string GetMsg()
        {
            if (string.IsNullOrEmpty(mMsg))
            {
                mMsg = id + ": " + msg;
            }
            return mMsg;
        }

        static public string GetIcon(MessageType pType)
        {
            switch (pType)
            {
                case MessageType.Info:
                    return "console.infoicon.sml";
                case MessageType.Warning:
                    return "console.warnicon.sml";
                case MessageType.Error:
                    return "console.erroricon.sml";
                default:
                    return string.Empty;
            }
        }

        public void Draw(int pIndex, AndroidLogCatWindow pInstance)
        {
            index = pIndex;

            var tPosition = new Rect(pInstance.mContentScrollPos, pInstance.mContentScrollSize);
            var tDrawRect = EditorGUILayout.GetControlRect(true, cItemHeight);
            if (!tPosition.Overlaps(tDrawRect)) return;

            if (pInstance.mSelectInfo == null)
            {
                pInstance.mSelectInfo = this;
            }

            var tColorStr = (pIndex & 1) == 1 ? "#DDDDDD" : "#9C9C9C";
            if (pInstance.selectId == id)
            {
                tColorStr = "#3E5F96";
            }
            var tColor = Color.grey;
            ColorUtility.TryParseHtmlString(tColorStr, out tColor);

            //以下绘制只能通过GUI的相关方法来操作，不能使用Layout相关的方法
            GUI.backgroundColor = tColor;
            EditorGUI.LabelField(tDrawRect, EditorGUIUtility.IconContent(Info.GetIcon(type)));
            var tTextRect = tDrawRect;
            var tPos = tTextRect.position;
            tPos.x += 20;
            tTextRect.position = tPos;
            EditorGUI.LabelField(tTextRect, new GUIContent(this.GetMsg()), pInstance.mContentStyle);

            var tBtnRect = tDrawRect;
            tBtnRect.size = new Vector2(tBtnRect.size.x, tBtnRect.size.y + 10);
            if (GUI.Button(tBtnRect, string.Empty, GUIStyle.none))
            {
                pInstance.mSelectInfo = this;
                pInstance.mDetailScrollPos = Vector2.zero;
                GUI.FocusControl(string.Empty);
                pInstance.Repaint();
            }
            GUI.backgroundColor = Color.white;
        }

        public bool MatchMasg(string mSearchText)
        {
            if (string.IsNullOrEmpty(mSearchText)) return true;
            return msg.IndexOf(mSearchText, StringComparison.OrdinalIgnoreCase) != -1;
        }
    }

    struct ThreadInfo
    {
        public int index { set; get; }
        public string[] msgs;
    }
    #endregion

    //适配不同log日志格式，如有新增格式，加新的正则即可
    static string[] sRegexs = new string[]
    {
        @"(?<type>\w+)/Unity\(\d+\):\s*(?<desc>.*)",
        @"(?<type>\w+)\sUnity\s+:\s*(?<desc>.*)"
    };

    List<Info> mInfos1;
    List<Info> mInfos2;
    List<Info> mInfos3;
    List<Info> mInfos4;
    int mThreadCount = 4;

    int mFinishFlag = 0;
    bool mRefresh = false;
    Vector2 mContentScrollPos;
    Vector2 mContentScrollSize;
    Vector2 mDetailScrollPos;
    int mSelectIndex = 0;
    Info mSelectInfo;
    float mSliderValue = 0.5F;
    string mSearchText = string.Empty;
    bool mNextFrameLocation = false;
    GUIStyle mContentStyle;
    List<Info> mTotalInfos = new List<Info>();
    List<Info> mInfos_Info = new List<Info>();
    List<Info> mInfos_Warning = new List<Info>();
    List<Info> mInfos_Error = new List<Info>();
    List<Info> mShowInfos = new List<Info>();
    List<string> mADBMsgs = new List<string>();

    //SplitterGUILayout BeginVerticalSplit
    MethodInfo mBeginVerticalSplit;
    //SplitterGUILayout EndVerticalSplit
    MethodInfo mEndVerticalSplit;
    //SplitterState relativeSizes
    FieldInfo mRelativeSizes;
    //SplitterState
    object mSplitterState;

    bool mEnableInfo = true;
    bool mEnableWarning = true;
    bool mEnableError = true;
    Process mADBProcess;

    public int selectId
    {
        get
        {
            return mSelectInfo == null ? -1 : mSelectInfo.id;
        }
    }

    [MenuItem("Window/AndroidLogCatWindow")]
    static public void ShowWindow()
    {
        var tView = CreateInstance<AndroidLogCatWindow>();
        tView.titleContent = new GUIContent("AndroidLogCat", "安卓日志工具.\n使用说明：\n1、从其他地方复制日志到系统剪贴板，点击Parse按钮，等待数秒后就会格式好所有的日志.\n2、支持adb连接实时打印日志" +
            "\n\n使用技巧：\n1、支持上下箭头、Home键、End键改变日志的显示.\n2、支持搜索指定关键字（不区分大小写），然后选择某条日志后，点输入框的×来定位到刚刚选中的日志.\n3、跳转到选中：置顶当前选中的日志\n\n目前已知问题：\n1、Unity2017.3.0f2以下版本存在部分中文乱码，建议用Unity2017.3.0f2及以上版本");
        tView.Show();
    }

    void OnEnable()
    {
        var tSplitterState = typeof(EditorWindow).Assembly.GetType("UnityEditor.SplitterState");
        mRelativeSizes = tSplitterState.GetField("relativeSizes", BindingFlags.Instance | BindingFlags.Public);
        mSplitterState = Activator.CreateInstance(tSplitterState, new object[] { new float[] { 70, 30 }, new int[] { 32, 32 }, null });

        var tSplitterGUILayoutType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SplitterGUILayout");
        mBeginVerticalSplit = tSplitterGUILayoutType.GetMethod("BeginVerticalSplit", BindingFlags.Static | BindingFlags.Public, null, new Type[] { tSplitterState, typeof(GUILayoutOption[]) }, null);
        mEndVerticalSplit = tSplitterGUILayoutType.GetMethod("EndVerticalSplit", BindingFlags.Static | BindingFlags.Public);
    }

    void OnGUI()
    {
        UpdateGUIStyle();
        DrawToolBar();

        if (mFinishFlag >= mThreadCount)
        {
            Debug.LogError("Parse Finish");

            mTotalInfos.AddRange(mInfos1);
            mTotalInfos.AddRange(mInfos2);
            mTotalInfos.AddRange(mInfos3);
            mTotalInfos.AddRange(mInfos4);
            mFinishFlag = 0;
            mRefresh = false;

            for (int i = 0, imax = mTotalInfos.Count; i < imax; i++)
            {
                var tInfo = mTotalInfos[i];
                tInfo.id = i;

                switch (tInfo.type)
                {
                    case MessageType.Info:
                        mInfos_Info.Add(tInfo);
                        break;
                    case MessageType.Warning:
                        mInfos_Warning.Add(tInfo);
                        break;
                    case MessageType.Error:
                        mInfos_Error.Add(tInfo);
                        break;
                }
            }

            UpdateInfos();
        }
        else if (mRefresh)
        {
            this.Repaint();
        }

        //Start VerticalSplit
        if (mBeginVerticalSplit != null && mSplitterState != null)
        {
            mBeginVerticalSplit.Invoke(null, new object[] { mSplitterState, null });
        }

        mContentScrollSize = new Vector2(position.size.x, (int)(position.size.y * (1 - mSliderValue)));
        mContentScrollPos = EditorGUILayout.BeginScrollView(mContentScrollPos, GUILayout.Height(mContentScrollSize.y));
        using (new EditorGUILayout.VerticalScope())
        {
            for (int i = 0, imax = mShowInfos.Count; i < imax; i++)
            {
                var item = mShowInfos[i];
                item.Draw(i, this);
            }
        }
        EditorGUILayout.EndScrollView();

        mDetailScrollPos = EditorGUILayout.BeginScrollView(mDetailScrollPos, GUILayout.Height(position.size.y * mSliderValue - 25));
        var tMsg = mSelectInfo == null ? string.Empty : mSelectInfo.GetTrace();
        EditorGUILayout.SelectableLabel(tMsg, EditorStyles.textArea, GUILayout.Height(GUI.skin.textArea.CalcHeight(new GUIContent(tMsg), position.width)));
        EditorGUILayout.EndScrollView();

        //End VerticalSplit
        if (mEndVerticalSplit != null)
        {
            mEndVerticalSplit.Invoke(null, null);
        }

        if (mRelativeSizes != null && mSplitterState != null)
        {
            var tValues = mRelativeSizes.GetValue(mSplitterState) as float[];
            mSliderValue = tValues[1];
        }

        if (mNextFrameLocation)
        {
            mNextFrameLocation = false;
            UpdateContentScrollPos();
        }

        var tKeyCode = Event.current.keyCode;
        switch (Event.current.type)
        {
            case EventType.KeyDown:
                if (tKeyCode == KeyCode.UpArrow)
                {
                    if (mSelectIndex > 0) mSelectIndex--;
                }
                else if (tKeyCode == KeyCode.DownArrow)
                {
                    if (mSelectIndex < mShowInfos.Count - 1)
                        mSelectIndex++;
                }
                else if (tKeyCode == KeyCode.Home)
                {
                    mSelectIndex = mShowInfos.Count > 0 ? 0 : -1;
                }
                else if (tKeyCode == KeyCode.End)
                {
                    mSelectIndex = mShowInfos.Count > 0 ? mShowInfos.Count - 1 : -1;
                }

                if (mSelectIndex >= 0 && mSelectIndex < mShowInfos.Count)
                {
                    mSelectInfo = mShowInfos[mSelectIndex];
                    UpdateContentScrollPos();
                    GUI.FocusControl(string.Empty);
                    Event.current.Use();
                }
                break;
        }

        if (Event.current.type == EventType.KeyUp) Repaint();
    }

    void OnDestroy()
    {
        StopADB();
    }

    void UpdateGUIStyle()
    {
        if (mContentStyle == null)
        {
            mContentStyle = new GUIStyle(EditorStyles.helpBox);
            mContentStyle.fontSize = 14;
            mContentStyle.wordWrap = false;
            mContentStyle.richText = true;
        }
        EditorStyles.textArea.wordWrap = true;
        EditorStyles.textArea.richText = true;
        EditorStyles.helpBox.wordWrap = false;
        EditorStyles.helpBox.richText = true;
    }

    /// <summary>
    /// 重置变量值
    /// </summary>
    void ResetVarliable()
    {
        mInfos1 = new List<Info>();
        mInfos2 = new List<Info>();
        mInfos3 = new List<Info>();
        mInfos4 = new List<Info>();
        mInfos_Info = new List<Info>();
        mInfos_Warning = new List<Info>();
        mInfos_Error = new List<Info>();
        mTotalInfos = new List<Info>();
        mShowInfos = new List<Info>();
        mADBMsgs = new List<string>();
        mDetailScrollPos = mContentScrollPos = Vector2.zero;
        mSelectIndex = mFinishFlag = 0;
        mSelectInfo = null;
    }

    /// <summary>
    /// 绘制ToolBar
    /// </summary>
    void DrawToolBar()
    {
        EditorGUI.BeginDisabledGroup(mRefresh);
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar, GUILayout.ExpandWidth(true)))
        {
            GUILayout.Label(new GUIContent("鼠标放上来查看说明", this.titleContent.tooltip), EditorStyles.toolbarButton, GUILayout.Width(100));
            GUILayout.Space(10);

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                ResetVarliable();
            }

            GUILayout.Space(10);

            GUI.backgroundColor = Color.red;
            EditorGUI.BeginDisabledGroup(mADBProcess != null);
            if (GUILayout.Button("Parse", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                ClickParseBtn();
            }
            EditorGUI.EndDisabledGroup();
            GUI.backgroundColor = Color.white;
            GUILayout.Space(10);

            if (GUILayout.Button("跳转到选中", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                UpdateContentScrollPos();
            }
            GUILayout.Space(10);

            var tContent = "日志总条数：" + mShowInfos.Count.ToString();
            EditorGUILayout.LabelField(tContent, GUILayout.Width(120));
            GUILayout.Space(10);


            EditorGUI.BeginDisabledGroup(mADBProcess != null);
            if (GUILayout.Button("StartADB", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                ResetVarliable();
                StartADB();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(mADBProcess == null);
            if (GUILayout.Button("StopADB", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                StopADB();
            }
            EditorGUI.EndDisabledGroup();

            GUI.changed = false;
            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandWidth(true)))
            {
                GUI.changed = false;
                mSearchText = EditorGUILayout.TextField(string.Empty, mSearchText, "ToolbarSeachTextField", GUILayout.MinWidth(50));
                if (GUILayout.Button(mSearchText, string.IsNullOrEmpty(mSearchText) ? "ToolbarSeachCancelButtonEmpty" : "ToolbarSeachCancelButton"))
                {
                    if (!string.IsNullOrEmpty(mSearchText))
                    {
                        mSearchText = string.Empty;
                        GUI.FocusControl(string.Empty);
                        mNextFrameLocation = true;
                    }
                }
            }
            GUILayout.Space(10);

            mEnableInfo = GUILayout.Toggle(mEnableInfo, new GUIContent(mInfos_Info.Count.ToString(), EditorGUIUtility.IconContent(Info.GetIcon(MessageType.Info)).image), EditorStyles.toolbarButton, GUILayout.Width(80));
            mEnableWarning = GUILayout.Toggle(mEnableWarning, new GUIContent(mInfos_Warning.Count.ToString(), EditorGUIUtility.IconContent(Info.GetIcon(MessageType.Warning)).image), EditorStyles.toolbarButton, GUILayout.Width(80));
            mEnableError = GUILayout.Toggle(mEnableError, new GUIContent(mInfos_Error.Count.ToString(), EditorGUIUtility.IconContent(Info.GetIcon(MessageType.Error)).image), EditorStyles.toolbarButton, GUILayout.Width(80));

            if (GUI.changed)
            {
                UpdateInfos();
            }
        }
        EditorGUI.EndDisabledGroup();
    }

    /// <summary>
    /// 点击了解析按钮
    /// </summary>
    void ClickParseBtn()
    {
        if (mRefresh) return;

        var tText = GUIUtility.systemCopyBuffer;
        if (string.IsNullOrEmpty(tText))
        {
            EditorUtility.DisplayDialog("提示", "剪贴板为空", "确定");
            return;
        }

        mRefresh = true;
        ResetVarliable();

        var tTexts = tText.Split('\r', '\n');
        var tLength = tTexts.Length;

        try
        {
            var list = new List<string[]>(mThreadCount);
            var tIndex = 0;
            for (int i = 0; i < mThreadCount; i++)
            {
                string[] array = null;
                if (i == mThreadCount - 1)
                {
                    var len = tLength - tIndex - 1;
                    array = new string[len];
                    Array.Copy(tTexts, tIndex, array, 0, len);
                }
                else
                {
                    var len = tLength / mThreadCount;
                    array = new string[len];
                    Array.Copy(tTexts, tIndex, array, 0, len);
                    tIndex += len;
                }

                list.Add(array);
            }

            for (int i = 0; i < mThreadCount; i++)
            {
                var th1 = new Thread(new ParameterizedThreadStart(ParseObject));
                th1.Start(new ThreadInfo() { index = i + 1, msgs = list[i] });
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            if (tLength == 0) mRefresh = false;
        }
    }

    /// <summary>
    /// 更新界面显示数据
    /// </summary>
    void UpdateInfos(bool pResetPos = true)
    {
        List<Info> tInfos = null;
        if (mEnableInfo && mEnableWarning && mEnableError)
        {
            tInfos = new List<Info>(mTotalInfos);
        }
        else
        {
            tInfos = new List<Info>();

            if (mEnableInfo) tInfos.AddRange(mInfos_Info);
            if (mEnableWarning) tInfos.AddRange(mInfos_Warning);
            if (mEnableError) tInfos.AddRange(mInfos_Error);
        }

        tInfos.Sort((x, y) => x.id.CompareTo(y.id));

        if (string.IsNullOrEmpty(mSearchText))
        {
            mShowInfos = new List<Info>(tInfos);
        }
        else
        {
            mShowInfos = new List<Info>();
            foreach (var item in tInfos)
            {
                if (item.MatchMasg(mSearchText))
                {
                    mShowInfos.Add(item);
                }
            }
        }

        if (pResetPos) mContentScrollPos = Vector2.zero;
    }

    /// <summary>
    /// 更新ScrollView位置
    /// </summary>
    void UpdateContentScrollPos()
    {
        var tCurrentRow = -1;
        for (int i = 0, imax = mShowInfos.Count; i < imax; i++)
        {
            if (mShowInfos[i].id == selectId)
            {
                tCurrentRow = i;
                break;
            }
        }
        if (tCurrentRow == -1) return;

        var tRowHeight = Info.cItemHeight;
        mContentScrollPos.y = tRowHeight * tCurrentRow;

        //因为现在每个Item间有个2pixel的间隔
        mContentScrollPos.y += tCurrentRow * 2;

        if (mContentScrollPos.y < 0)
            mContentScrollPos.y = 0;
    }

    /// <summary>
    /// 解析日志
    /// </summary>
    /// <param name="pObject"></param>
    void ParseObject(object pObject)
    {
        var tInfo = (ThreadInfo)pObject;
        List<Info> tInfoList = null;

        switch (tInfo.index)
        {
            case 1:
                tInfoList = mInfos1;
                break;
            case 2:
                tInfoList = mInfos2;
                break;
            case 3:
                tInfoList = mInfos3;
                break;
            case 4:
                tInfoList = mInfos4;
                break;
            default:
                throw new NotImplementedException();
        }

        var tMsg = string.Empty;
        var tMsgType = MessageType.None;
        var tTrace = string.Empty;
        for (int i = 0, imax = tInfo.msgs.Length; i < imax; i++)
        {
            var tStr = tInfo.msgs[i];
            if (string.IsNullOrEmpty(tStr)) continue;

            Match tMatch = null;
            foreach (var item in sRegexs)
            {
                tMatch = Regex.Match(tStr, item);
                if (tMatch.Success) break;
            }

            if (!tMatch.Success) continue;

            tStr = tMatch.Groups["desc"].Value;
            var tType = tMatch.Groups["type"].Value;
            switch (tType)
            {
                case "I":
                    tMsgType = MessageType.Info;
                    break;
                case "W":
                    tMsgType = MessageType.Warning;
                    break;
                case "E":
                    tMsgType = MessageType.Error;
                    break;
            }
            if (string.IsNullOrEmpty(tStr)) continue;

            if (string.IsNullOrEmpty(tMsg))
            {
                tMsg = tStr;
            }
            else
            {
                tTrace += tStr + "\n";
            }

            if (tStr.IndexOf("(Filename:") != -1)
            {
                if (!string.IsNullOrEmpty(tMsg.Trim()) && !string.IsNullOrEmpty(tTrace.Trim()))
                {
                    tInfoList.Add(new Info() { msg = tMsg, trace = tTrace, type = tMsgType });
                }
                tMsg = string.Empty;
                tTrace = string.Empty;
            }
        }

        mFinishFlag++;
    }

    /// <summary>
    /// 开始监听ADB
    /// </summary>
    void StartADB()
    {
        if (mADBProcess != null) return;

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        mADBProcess = new Process();
        mADBProcess.StartInfo.FileName = "adb.exe";
        mADBProcess.StartInfo.Arguments = "shell";
        mADBProcess.StartInfo.UseShellExecute = false;
        mADBProcess.StartInfo.CreateNoWindow = true;
        mADBProcess.StartInfo.RedirectStandardError = true;
        mADBProcess.StartInfo.RedirectStandardOutput = true;
        mADBProcess.StartInfo.RedirectStandardInput = true;
        mADBProcess.OutputDataReceived += OnReceived;
        mADBProcess.ErrorDataReceived += OnReceived;
        mADBProcess.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        mADBProcess.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        mADBProcess.Start();

        using (var tStream = mADBProcess.StandardInput)
        {
            tStream.WriteLine("logcat -s Unity");
            tStream.Close();
        }

        mADBProcess.BeginOutputReadLine();
        mADBProcess.BeginErrorReadLine();

        EditorApplication.update -= ParseADBMsg;
        EditorApplication.update += ParseADBMsg;
    }

    /// <summary>
    /// 停止监听ADB
    /// </summary>
    void StopADB()
    {
        if (mADBProcess == null) return;

        try
        {
            if (mADBProcess != null && !mADBProcess.HasExited)
            {
                mADBProcess.CancelOutputRead();
                mADBProcess.CancelErrorRead();
                mADBProcess.Kill();
            }

            EditorApplication.update -= ParseADBMsg;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            mADBProcess = null;
        }
    }

    void OnReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;

        var tMsg = e.Data;

#if !UNITY_2017_3_OR_NEWER
        var tBytes = Encoding.Default.GetBytes(tMsg);
        tMsg = Encoding.UTF8.GetString(tBytes);
#endif

        lock (mADBMsgs)
        {
            mADBMsgs.Add(tMsg);
        }
    }

    void ParseADBMsg()
    {
        if (mADBProcess == null) return;

        if (mADBProcess.HasExited)
        {
            StopADB();
            return;
        }

        lock (mADBMsgs)
        {
            if (mADBMsgs.Count == 0) return;

            var tMsg = string.Empty;
            var tMsgType = MessageType.None;
            var tTrace = string.Empty;
            var tLastIndex = 0;
            var tChange = false;
            for (int i = 0, imax = mADBMsgs.Count; i < imax; i++)
            {
                var tStr = mADBMsgs[i];
                if (string.IsNullOrEmpty(tStr)) continue;

                Match tMatch = null;
                foreach (var item in sRegexs)
                {
                    tMatch = Regex.Match(tStr, item);
                    if (tMatch.Success) break;
                }

                if (!tMatch.Success) continue;

                tStr = tMatch.Groups["desc"].Value;
                var tType = tMatch.Groups["type"].Value;
                switch (tType)
                {
                    case "I":
                        tMsgType = MessageType.Info;
                        break;
                    case "W":
                        tMsgType = MessageType.Warning;
                        break;
                    case "E":
                        tMsgType = MessageType.Error;
                        break;
                }
                if (string.IsNullOrEmpty(tStr)) continue;

                if (string.IsNullOrEmpty(tMsg))
                {
                    tMsg = tStr;
                }
                else
                {
                    tTrace += tStr + "\n";
                }

                if (tStr.IndexOf("(Filename:") != -1)
                {
                    if (!string.IsNullOrEmpty(tMsg.Trim()) && !string.IsNullOrEmpty(tTrace.Trim()))
                    {
                        var tInfo = new Info() { msg = tMsg, trace = tTrace, type = tMsgType, id = mTotalInfos.Count };
                        mTotalInfos.Add(tInfo);
                        switch (tMsgType)
                        {
                            case MessageType.Info:
                                mInfos_Info.Add(tInfo);
                                break;
                            case MessageType.Warning:
                                mInfos_Warning.Add(tInfo);
                                break;
                            case MessageType.Error:
                                mInfos_Error.Add(tInfo);
                                break;
                        }
                    }
                    tMsg = string.Empty;
                    tTrace = string.Empty;
                    tLastIndex = i;
                    tChange = true;
                }
            }

            if (tChange)
            {
                UpdateInfos(false);
                mADBMsgs.RemoveRange(0, tLastIndex + 1);
                this.Repaint();
            }
        }
    }
}