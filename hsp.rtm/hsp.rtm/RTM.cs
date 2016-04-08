﻿using System;
using System.Linq;
using Microsoft.CSharp;
using System.Windows.Forms;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace hsp.rtm
{
    /// <summary>
    /// HSPの変数の実体を管理するクラス
    /// </summary>
    public static class Manager
    {
        //HSPの変数の実体
        //キーが変数名
        //バリューが実際の値
        public static Dictionary<string, dynamic> Variables = new Dictionary<string, dynamic>(); 
    }

    /// <summary>
    /// Watcherから送信されてくるコードを受け取るクラス
    /// </summary>
    public class WindowMessage
    {
        private struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public Int32 cbData;
            public string lpData;
        }

        public event EventHandler<ReceiveDataEventArgs> OnReceiveData;

        /// <summary>
        /// Watcherから送信されてくるデータを受け取る
        /// </summary>
        /// <param name="m">処理するためのMessageインスタンス</param>
        public void ReceiveData(Message m)
        {
            if (OnReceiveData != null)
            {
                var cds = (COPYDATASTRUCT)m.GetLParam(typeof(COPYDATASTRUCT));
                OnReceiveData(this, new ReceiveDataEventArgs(cds.lpData));
            }
        }
    }

    public class ReceiveDataEventArgs : EventArgs
    {
        public ReceiveDataEventArgs(string data)
        {
            ReceiveData = data;
        }

        public string ReceiveData { get; }
    }

    public class Window : Form
    {
        private const int WM_COPYDATA = 0x4A;

        private WindowMessage message { get; set; }

        public void _Load(object sender, EventArgs e)
        {
            message = new WindowMessage();
            message.OnReceiveData += (_o, _e) => RTM.Execute(_e.ReceiveData);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_COPYDATA)
            {
                message.ReceiveData(m);
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// watcherを終了させる
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public void ExitWatcher(object sender, EventArgs e)
        {
            try
            {
                var watcherID = GetParentProcess(Process.GetCurrentProcess().Id);
                var watcher = Process.GetProcessById(watcherID);
                if (watcher.ProcessName.Equals("hsp.watcher"))
                {
                    watcher.Kill();
                }
            }
            catch (Exception ex)
            {
                Error.AlertError(ex);
            }
        }

        /// <summary>
        /// 親プロセスのIDを得る
        /// </summary>
        /// <param name="Id">自分のプロセスID</param>
        /// <returns></returns>
        public static int GetParentProcess(int Id)
        {
            var parentPid = 0;
            using (var mo = new ManagementObject("win32_process.handle='" + Id + "'"))
            {
                mo.Get();
                parentPid = Convert.ToInt32(mo["ParentProcessId"]);
            }
            return parentPid;
        }
    }

    public static class RTM
    {
        private static dynamic instance;
        private static dynamic oldInstance;

        /// <summary>
        /// 動的にイベントを追加
        /// </summary>
        /// <param name="eventName">追加するイベント名</param>
        /// <param name="eventType">追加するイベントハンドラの型情報</param>
        /// <param name="instanceMethodName">追加するイベントのインスタンス内での名前</param>
        public static void AddEvent(string eventName, Type eventType, string instanceMethodName)
        {
            Core.MainWindow.GetType()
                .GetEvent(eventName)
                .AddEventHandler(Core.MainWindow,
                    Delegate.CreateDelegate(eventType, instance, instance.GetType().GetMethod(instanceMethodName)));
        }

        /// <summary>
        /// 動的にイベントを削除
        /// </summary>
        /// <param name="eventName">削除するイベント名</param>
        /// <param name="eventType">削除するイベントハンドラの型情報</param>
        /// <param name="instanceMethodName">削除するイベントのインスタンス内での名前</param>
        public static void DeleteEvent(string eventName, Type eventType, string instanceMethodName)
        {
            Core.MainWindow.GetType()
                .GetEvent(eventName)
                .RemoveEventHandler(Core.MainWindow,
                    Delegate.CreateDelegate(eventType, oldInstance, oldInstance.GetType().GetMethod(instanceMethodName)));
        }

        public static void Execute(string str)
        {
            try
            {
                //多分他にも初期化しないといけないものある
                //変数リストを初期化
                Analyzer.VariableList = new List<string>()
                {
                    "strsize",
                    "stat",
                    "cnt"
                };

                //配列のリストを初期化
                Analyzer.ArrayVariableList = new List<string>();

                //全角スペースとタブを半角スペースに変換し, 改行でスプリット
                var hspArrayData = str.Split('\n').Where(i => i.Length != 0).ToList();

                //HSPのコードをC#のコードに変換
                var code = Analyzer.GenerateCode(hspArrayData);

                //更新された変数リストをもとに, Manager.Variablesを更新する
                foreach (var variableName in Manager.Variables.Keys.ToList())
                {
                    //変数が使われなくなった場合, Dictionaryから削除
                    if (!Analyzer.VariableList.Contains(variableName))
                    {
                        Manager.Variables.Remove(variableName);
                    }
                }
                //Manager.Variablesに存在しない変数が定義された場合は追加する
                foreach (var variableName in Analyzer.VariableList)
                {
                    if (!Manager.Variables.Keys.ToList().Contains(variableName))
                    {
                        //変数の追加
                        //初期値はnullにしてるけど, 大丈夫？
                        Manager.Variables.Add(variableName, null);
                    }
                }

                //デバッグ用のコード出力
                var sw = new StreamWriter("code.cs", false, Encoding.UTF8);
                sw.WriteLine(code);
                sw.Close();

                //生成したコードを実行
                var param = new CompilerParameters();

                param.ReferencedAssemblies.AddRange(new[]
                {
                    "mscorlib.dll", "System.dll", "System.Core.dll", "Microsoft.CSharp.dll", "System.IO.dll",
                    "System.Windows.Forms.dll", "System.Drawing.dll", "System.Linq.dll"
                });

                //GUIアプリケーションとしてコンパイルするためのオプション
                param.CompilerOptions = "/t:winexe";

                //生成したコードをコンパイルしてAssemblyを得る
                var assembly = new CSharpCodeProvider()
                    .CompileAssemblyFromSource(param, code)
                    .CompiledAssembly;

                //Programの型情報を取得
                var dataType = assembly.GetType("NameSpace.Program");

                //前のインスタンスをバックアップ
                oldInstance = instance;

                //Programのインスタンスを作成
                instance = Activator.CreateInstance(dataType, new object[]{ Core.MainWindow , Manager.Variables, Core.DebugWindow });

                //既に追加されているイベントを破棄
                if (oldInstance != null)
                {
                    DeleteEvent("Paint", typeof(PaintEventHandler), "Paint");
                }
                //新しくコンパイルしたイベントを追加
                AddEvent("Paint", typeof(PaintEventHandler), "Paint");
                //リフレッシュ
                Core.MainWindow.Refresh();
            }
            catch (Exception ex)
            {
                //何かしらのエラー
                //構文エラーとかは別途で警告出したい
                Error.AlertError(ex);
            }
        }
    }
}