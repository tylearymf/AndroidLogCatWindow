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
        public string tag { set; private get; }
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
                mTrace = Regex.Unescape(mTrace);
            }
            return mTrace;
        }

        public string GetMsg()
        {
            if (string.IsNullOrEmpty(mMsg))
            {
                mMsg = string.Format("{0} {1}: {2}", id, tag, msg);
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
            tBtnRect.position = new Vector2(tBtnRect.position.x, tBtnRect.position.y - 5);
            tBtnRect.size = new Vector2(tBtnRect.size.x, tBtnRect.size.y + 5);
            if (GUI.Button(tBtnRect, string.Empty, GUIStyle.none))
            {
                pInstance.mSelectIndex = index;
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

    const string cUnityTag = "Unity";
    //适配不同log日志格式，如有新增格式，加新的正则即可
    static string[] sRegexs = new string[]
    {
        @"(?<type>\w+)/{0}\s*\(\d+\):(?<spaceDesc>\s*)(?<desc>.*)",
        @"(?<type>\w+)\s*{0}\s*:(?<spaceDesc>\s*)(?<desc>.*)",
        @"(?<type>\w+)/{0}\s*.*?\s*:(?<spaceDesc>\s*)(?<desc>.*)",
    };

    List<Info> mInfos1;
    List<Info> mInfos2;
    List<Info> mInfos3;
    List<Info> mInfos4;
    static int mThreadCount = 4;

    int mFinishFlag = 0;
    bool mLockUI = false;
    DateTime mLockUIStartTime;
    int mLockUISecond;
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

    List<string> mTags = new List<string>();
    public List<string> tags
    {
        private set
        {
            mTags = value;
        }
        get
        {
            return mTags;
        }
    }

    public int selectId
    {
        get
        {
            return mSelectInfo == null ? -1 : mSelectInfo.id;
        }
    }

    [MenuItem("Window/AndroidLogCatWindow(安卓日志窗口)")]
    static public void ShowWindow()
    {
        var tView = CreateInstance<AndroidLogCatWindow>();
        tView.titleContent = new GUIContent(
            "AndroidLogCat", "安卓日志工具." +
            "\n使用说明：" +
            "\n1、从其他地方复制日志到系统剪贴板，点击转换按钮，等待数秒后就会格式好所有的日志." +
            "\n2、支持adb连接实时打印日志" +
            "\n\n使用技巧：" +
            "\n1、支持上下箭头、Home键、End键改变日志的显示." +
            "\n2、支持搜索指定关键字（不区分大小写），然后选择某条日志后，点输入框的×来定位到刚刚选中的日志." +
            "\n3、跳转到选中：置顶当前选中的日志" +
            "\n4、支持显示指定标签，多个标签用|分隔" +
            "\n\n目前已知问题：" +
            "\n1、Unity2017.3.0f2以下版本存在部分中文乱码，建议用Unity2017.3.0f2及以上版本");
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

        UpdateTags();
    }

    void OnGUI()
    {
        UpdateGUIStyle();
        DrawToolBar();

        if (mFinishFlag >= mThreadCount)
        {
            Debug.LogError(string.Format("转换完成.耗时：{0}s", mLockUISecond));

            mTotalInfos.AddRange(mInfos1);
            mTotalInfos.AddRange(mInfos2);
            mTotalInfos.AddRange(mInfos3);
            mTotalInfos.AddRange(mInfos4);
            ParseFinish();

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
        else if (mLockUI)
        {
            var tSecond = Mathf.CeilToInt((float)(DateTime.Now - mLockUIStartTime).TotalSeconds);
            if (mLockUISecond != tSecond)
            {
                mLockUISecond = tSecond;
                ShowNotification(new GUIContent(string.Format("正在解析({0}s) ", mLockUISecond)));
            }
            this.Repaint();
        }

        EditorGUI.BeginDisabledGroup(mLockUI);

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
        EditorGUI.EndDisabledGroup();

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
                var tChange = false;
                if (tKeyCode == KeyCode.UpArrow)
                {
                    if (mSelectIndex > 0)
                    {
                        mSelectIndex--;
                        tChange = true;
                    }
                }
                else if (tKeyCode == KeyCode.DownArrow)
                {
                    if (mSelectIndex < mShowInfos.Count - 1)
                    {
                        mSelectIndex++;
                        tChange = true;
                    }
                }
                else if (tKeyCode == KeyCode.Home)
                {
                    mSelectIndex = mShowInfos.Count > 0 ? 0 : -1;
                    tChange = true;
                }
                else if (tKeyCode == KeyCode.End)
                {
                    mSelectIndex = mShowInfos.Count > 0 ? mShowInfos.Count - 1 : -1;
                    tChange = true;
                }

                if (tChange && mSelectIndex >= 0 && mSelectIndex < mShowInfos.Count)
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

    void UpdateTags()
    {
        var tHashSet = new HashSet<string>();
        foreach (var item in tags)
        {
            if (string.IsNullOrEmpty(item)) continue;
            tHashSet.Add(item);
        }
        if (tHashSet.Count == 0)
        {
            tags.Add(cUnityTag);
        }
        else
        {
            tags = tHashSet.ToList();
        }
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
        EditorGUI.BeginDisabledGroup(mLockUI);
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar, GUILayout.ExpandWidth(true)))
        {
            GUILayout.Label(new GUIContent("鼠标放上来查看说明", this.titleContent.tooltip), EditorStyles.toolbarButton, GUILayout.Width(100));
            GUILayout.Space(10);

            if (GUILayout.Button("标签", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                InputMiniWindow.ShowWindow<string>(new GUIContent("输入多个标签，格式以|分割"), new Vector2(300, 40), string.Join("|", tags.ToArray()), (pConfirm, pResult, pView) =>
                {
                    if (pConfirm && pResult != null)
                    {
                        var tNewTags = new HashSet<string>();
                        var tResult = pResult as string;
                        var tSplits = tResult.Split('|');
                        var tError = false;
                        foreach (var item in tSplits)
                        {
                            if (!string.IsNullOrEmpty(item))
                            {
                                tError = !Regex.IsMatch(item, @"^[a-zA-Z0-9]+$");

                                if (tError) break;
                                else
                                {
                                    tNewTags.Add(item);
                                }
                            }
                        }

                        if (tError)
                        {
                            Debug.LogError("格式错误！！！标签只能包含字母和数字");
                        }
                        else
                        {
                            tags = tNewTags.ToList();
                            pView.Close();
                        }
                    }
                    else
                    {
                        pView.Close();
                    }
                });
            }

            GUILayout.Space(10);
            if (GUILayout.Button("清除", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                ResetVarliable();
            }

            GUILayout.Space(10);
            GUI.backgroundColor = Color.red;
            EditorGUI.BeginDisabledGroup(mADBProcess != null);
            if (GUILayout.Button("转换", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                if (EditorUtility.DisplayDialog("提示", "是否确认转换剪贴板中的日志", "确定", "取消"))
                {
                    ClickParseBtn();
                }
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
                mNextFrameLocation = true;
            }
        }
        EditorGUI.EndDisabledGroup();
    }

    /// <summary>
    /// 点击了解析按钮
    /// </summary>
    void ClickParseBtn()
    {
        if (mLockUI) return;

        var tText = GUIUtility.systemCopyBuffer;
        if (string.IsNullOrEmpty(tText))
        {
            EditorUtility.DisplayDialog("提示", "剪贴板为空", "确定");
            return;
        }

        mLockUI = true;
        mLockUISecond = -1;
        mLockUIStartTime = DateTime.Now;
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
            if (tLength == 0)
            {
                ParseFinish();
            }
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

        mSelectIndex = tCurrentRow;

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
        var tTrace = string.Empty;
        for (int i = 0, imax = tInfo.msgs.Length; i < imax; i++)
        {
            HandleMessage(tInfo.msgs[i], (pMsgType, pInfo) => { tInfoList.Add(pInfo); }, ref tMsg, ref tTrace);
        }

        mFinishFlag++;
    }

    /// <summary>
    /// 处理单条日志
    /// </summary>
    /// <param name="pFullMsg"></param>
    /// <param name="pCallBack"></param>
    /// <param name="pShortMsg"></param>
    /// <param name="pTrace"></param>
    /// <returns></returns>
    bool HandleMessage(string pFullMsg, Action<MessageType, Info> pCallBack, ref string pShortMsg, ref string pTrace)
    {
        if (string.IsNullOrEmpty(pFullMsg)) return false;

        Match tMatch = null;
        var tTag = string.Empty;
        foreach (var tag in tags)
        {
            if (pFullMsg.IndexOf(tag) == -1)
            {
                continue;
            }

            var tIsMatch = false;
            foreach (var item in sRegexs)
            {
                tTag = tag;
                tMatch = Regex.Match(pFullMsg, string.Format(item, tag));
                if (tMatch.Success)
                {
                    tIsMatch = true;
                    break;
                }
            }

            if (tIsMatch) break;
        }

        if (tMatch == null || !tMatch.Success) return false;

        pFullMsg = tMatch.Groups["desc"].Value;
        var tType = tMatch.Groups["type"].Value;
        var tMsgType = MessageType.None;
        switch (tType)
        {
            //Info
            case "I":
            //Verbose
            case "V":
            //Debug
            case "D":
                tMsgType = MessageType.Info;
                break;
            //Warning
            case "W":
                tMsgType = MessageType.Warning;
                break;
            //Assert
            case "A":
            //Error
            case "E":
                tMsgType = MessageType.Error;
                break;
        }
        if (tMsgType == MessageType.None || string.IsNullOrEmpty(pFullMsg)) return false;

        if (string.IsNullOrEmpty(pShortMsg))
        {
            pShortMsg = pFullMsg;
        }
        else
        {
            pTrace += tMatch.Groups["spaceDesc"].Value + pFullMsg + "\n";
        }

        if ((tTag == cUnityTag && pFullMsg.IndexOf("(Filename:") != -1))
        {
            if (!string.IsNullOrEmpty(pShortMsg.Trim()) && !string.IsNullOrEmpty(pTrace.Trim()))
            {
                var tInfo = new Info() { msg = pShortMsg, trace = pTrace, type = tMsgType, tag = tTag };
                pCallBack(tMsgType, tInfo);
            }
            pShortMsg = string.Empty;
            pTrace = string.Empty;
        }
        else if (tTag != cUnityTag)
        {
            if (!string.IsNullOrEmpty(pShortMsg.Trim()))
            {
                var tInfo = new Info() { msg = pShortMsg, trace = pTrace, type = tMsgType, tag = tTag };
                pCallBack(tMsgType, tInfo);
            }
            pShortMsg = string.Empty;
            pTrace = string.Empty;
        }

        return true;
    }

    void ParseFinish()
    {
        mFinishFlag = 0;
        mLockUI = false;
        mLockUISecond = 0;
        RemoveNotification();
    }

    #region ADB
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
            tStream.WriteLine("logcat");
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
            var tTrace = string.Empty;
            var tLastIndex = 0;
            var tChange = false;
            for (int i = 0, imax = mADBMsgs.Count; i < imax; i++)
            {
                var tResult = HandleMessage(mADBMsgs[i], (pMsgType, pInfo) =>
                 {
                     pInfo.id = mTotalInfos.Count;
                     mTotalInfos.Add(pInfo);
                     switch (pMsgType)
                     {
                         case MessageType.Info:
                             mInfos_Info.Add(pInfo);
                             break;
                         case MessageType.Warning:
                             mInfos_Warning.Add(pInfo);
                             break;
                         case MessageType.Error:
                             mInfos_Error.Add(pInfo);
                             break;
                     }

                 }, ref tMsg, ref tTrace);

                if (tResult)
                {
                    tLastIndex = i;
                }

                tChange |= tResult;
            }

            if (tChange)
            {
                UpdateInfos(false);
                mADBMsgs.RemoveRange(0, tLastIndex + 1);
                Repaint();
            }
        }
    }
    #endregion ADB
}

#region InputMiniWindow
class InputMiniWindow : EditorWindow
{
    static public void ShowWindow<T>(GUIContent pTitle, Vector2 pSize, object pDefaultValue, Action<bool, object, EditorWindow> pCallBack)
    {
        var tView = CreateInstance(typeof(InputMiniWindow)) as InputMiniWindow;
        tView.titleContent = pTitle;
        tView.mType = typeof(T);
        tView.mDefaultValue = pDefaultValue;
        tView.mCallBack = pCallBack;
        tView.ShowUtility();
        tView.minSize = pSize;
        tView.maxSize = pSize;
    }

    Type mType;
    object mDefaultValue;
    Action<bool, object, EditorWindow> mCallBack;

    void OnGUI()
    {
        using (new EditorGUILayout.VerticalScope())
        {
            DrawUI();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("确定"))
                {
                    CallBack(true);
                }
                if (GUILayout.Button("取消"))
                {
                    CallBack(false);
                }
            }
        }
    }

    void DrawUI()
    {
        if (mType == typeof(string))
        {
            SetValue(EditorGUILayout.TextField(GetValue() as string ?? string.Empty));
        }
    }

    void SetValue(object pValue)
    {
        mDefaultValue = pValue;
    }

    object GetValue()
    {
        return mDefaultValue;
    }

    void CallBack(bool pConfirm)
    {
        if (mCallBack != null)
        {
            mCallBack(pConfirm, GetValue(), this);
        }
    }
}
#endregion InputMiniWindow