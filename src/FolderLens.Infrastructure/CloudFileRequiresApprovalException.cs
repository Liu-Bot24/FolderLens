namespace FolderLens.Infrastructure;
public sealed class CloudFileRequiresApprovalException():IOException("文件内容仅在线上，请点击“读取在线文件”后预览。");
