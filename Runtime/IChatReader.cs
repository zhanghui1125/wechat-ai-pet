using System.Windows.Automation;

namespace WeChatPet.Runtime {
    public interface IChatReader {
        ChatSnapshot Read(long activity);
        AutomationElement Field {get;}
        string UiDiagnostics {get;}
    }
}
