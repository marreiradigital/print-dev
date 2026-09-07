; ============================================================================
;  Print Dev - script do instalador (Inno Setup 6)
;
;  Gere com:  powershell -ExecutionPolicy Bypass -File scripts\gerar-instalador.ps1
;  (o script publica o executavel antes de compilar; compilar este arquivo
;   sozinho usaria a publicacao anterior, que pode estar velha)
; ============================================================================

#define Nome        "Print Dev"
#define Versao      "0.1.0"
#define Fabricante  "Marreira Digital"
#define Executavel  "PrintDev.exe"
#define Site        "https://marreiradigital.com.br"

[Setup]
; Identidade fixa do produto. NAO mudar entre versoes: e por ela que o Windows
; reconhece uma atualizacao em vez de instalar uma segunda copia lado a lado.
AppId={{7B3F1C2E-9A44-4E17-B0D6-2C5A8E1F4D93}
AppName={#Nome}
AppVersion={#Versao}
AppVerName={#Nome} {#Versao}
AppPublisher={#Fabricante}
AppPublisherURL={#Site}
VersionInfoVersion={#Versao}

; Instalacao POR USUARIO, sem pedir administrador. Um capturador de tela nao
; precisa de privilegio nenhum para funcionar, e exigir UAC na instalacao seria
; cobrar um custo sem entregar nada em troca.
; Com este modo, {autopf} aponta para %LOCALAPPDATA%\Programs.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

DefaultDirName={autopf}\PrintDev
DefaultGroupName={#Nome}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; O programa e compilado para 64 bits (varias estruturas de interoperacao tem
; tamanho diferente em 32).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; O proprio programa usa este mutex para garantir instancia unica. Declarado
; aqui, o instalador DETECTA que ele esta aberto e pede para fechar, em vez de
; falhar na copia do arquivo travado - que e o erro mais comum de atualizar um
; programa que fica na bandeja.
AppMutex=PrintDev.InstanciaUnica

OutputDir=..\publish\installer
OutputBaseFilename=PrintDev-Setup-{#Versao}
SetupIconFile=..\src\PrintDev.App\Assets\PrintDev.ico
UninstallDisplayIcon={app}\{#Executavel}
UninstallDisplayName={#Nome}

WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
DisableWelcomePage=no
LicenseFile=
InfoBeforeFile=

; Windows 10 build 19041 (2004) e o minimo: e o que o reconhecimento de texto
; nativo exige.
MinVersion=10.0.19041

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[CustomMessages]
brazilianportuguese.CriarAtalhoAreaTrabalho=Criar um atalho na &Área de Trabalho
brazilianportuguese.IniciarComWindows=&Iniciar o Print Dev junto com o Windows
brazilianportuguese.GrupoOpcoes=Opções adicionais:
brazilianportuguese.ExecutarApos=Executar o {#Nome} agora
brazilianportuguese.ManterDados=Manter minhas configurações e o histórico de logs?
brazilianportuguese.ManterDadosDetalhe=Suas capturas em Imagens nunca são removidas pelo desinstalador.

[Tasks]
Name: "desktopicon"; Description: "{cm:CriarAtalhoAreaTrabalho}"; GroupDescription: "{cm:GrupoOpcoes}"; Flags: unchecked
; Marcada por padrao: um capturador de tela que nao esta rodando nao serve para
; nada - a tecla so funciona com o programa na bandeja.
Name: "autostart"; Description: "{cm:IniciarComWindows}"; GroupDescription: "{cm:GrupoOpcoes}"

[Files]
Source: "..\publish\win-x64\{#Executavel}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\win-x64\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; DestName: "LEIAME.md"; Flags: ignoreversion

[Icons]
Name: "{group}\{#Nome}"; Filename: "{app}\{#Executavel}"
Name: "{group}\Configurações do {#Nome}"; Filename: "{app}\{#Executavel}"; Parameters: "--configuracoes"
Name: "{autodesktop}\{#Nome}"; Filename: "{app}\{#Executavel}"; Tasks: desktopicon

[Registry]
; A entrada de inicializacao e escrita SO se a tarefa estiver marcada, e sai
; sozinha na desinstalacao. O argumento --silencioso e o que faz o programa subir
; direto para a bandeja, sem abrir janela no logon.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "PrintDev"; \
    ValueData: """{app}\{#Executavel}"" --silencioso"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#Executavel}"; Parameters: "--silencioso"; \
    Description: "{cm:ExecutarApos}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Fecha o programa antes de remover os arquivos. Sem isto, desinstalar com ele
; aberto deixa o executavel para tras e o icone fantasma na bandeja.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#Executavel} /F"; Flags: runhidden; RunOnceId: "FecharPrintDev"

[Code]
{
  Na desinstalacao, pergunta o que fazer com os dados do usuario.

  As CAPTURAS nunca sao tocadas: sao arquivos que a pessoa produziu e podem estar
  em qualquer pasta que ela escolheu. Apagar trabalho do usuario porque ele
  desinstalou um programa e a coisa errada a fazer, mesmo com aviso.
}
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Pasta: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Pasta := ExpandConstant('{userappdata}\PrintDev');

    if DirExists(Pasta) then
    begin
      if MsgBox(ExpandConstant('{cm:ManterDados}') + #13#10#13#10 +
                ExpandConstant('{cm:ManterDadosDetalhe}'),
                mbConfirmation, MB_YESNO) = IDNO then
      begin
        DelTree(Pasta, True, True, True);
      end;
    end;
  end;
end;
