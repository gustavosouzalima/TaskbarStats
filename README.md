# TaskbarStats

Mostra o uso de **CPU**, **RAM** e **GPU** (uso e temperatura) na barra de tarefas do Windows, ao lado do relógio. Discreto, sempre visível, sem precisar de administrador.

```
CPU 12%   GPU  8%      ^  📶 🔊   09:31
RAM 41%       52°C                20/10/2026
```

## Recursos

- CPU: mesmo contador do Gerenciador de Tarefas (`% Processor Utility`, via PDH).
- RAM: "Em uso" como no Gerenciador de Tarefas: (total − disponível) / total (`GlobalMemoryStatusEx`).
- GPU: uso calculado como o Gerenciador de Tarefas (contadores `GPU Engine`: soma dos processos por motor, mostra o motor mais ocupado).
- GPU NVIDIA: temperatura e VRAM via NVML (`nvml.dll`, instalado com o driver).
- Fundo copia a cor da barra de tarefas; texto claro ou escuro conforme o tema.
- Valores altos ficam em laranja (CPU/RAM ≥ 90%, GPU ≥ 95%, temperatura ≥ 83 °C).
- Some automaticamente quando um app está em tela cheia (jogos, vídeos).
- Passe o mouse para ver detalhes (RAM em GB, nome da GPU, VRAM).
- **Arrastar** com o botão esquerdo muda a posição (fica salva).
- **Duplo clique** abre o Gerenciador de Tarefas.
- **Botão direito**: iniciar com o Windows, voltar à posição padrão, sair.
- Roda sem UAC/administrador. Nenhum dado sai do PC.

## Limitações

- Temperatura da CPU não é exibida (exige driver em modo kernel / administrador).
- Temperatura e VRAM da GPU só em placas NVIDIA. O uso (%) funciona em qualquer GPU.
- Pensado para a barra de tarefas na parte de baixo do monitor principal.

## Instalação manual (passo a passo)

Todos os comandos são para **PowerShell** comum (não precisa abrir como administrador).

### 1. Pré-requisitos

Instale o Git e o .NET 9 SDK (pule o que já tiver):

```powershell
winget install --id Git.Git -e
winget install --id Microsoft.DotNet.SDK.9 -e
```

Feche e abra o PowerShell de novo para atualizar o PATH. Confira:

```powershell
git --version
dotnet --list-sdks    # deve listar uma versão 9.x
```

### 2. Baixar o código

```powershell
cd $env:USERPROFILE
git clone https://github.com/gustavosouzalima/TaskbarStats.git
cd TaskbarStats
```

### 3. Compilar e instalar

```powershell
dotnet publish src -c Release -o "$env:LOCALAPPDATA\Programs\TaskbarStats"
```

### 4. Abrir

```powershell
Start-Process "$env:LOCALAPPDATA\Programs\TaskbarStats\TaskbarStats.exe"
```

Os números devem aparecer à esquerda da bandeja (ícones perto do relógio). Se ficar em cima de algum ícone, arraste para o lado.

### 5. Iniciar junto com o Windows

Botão direito no TaskbarStats → **Iniciar com o Windows**. Ou pelo PowerShell:

```powershell
New-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
  -Name TaskbarStats -PropertyType String -Force `
  -Value "`"$env:LOCALAPPDATA\Programs\TaskbarStats\TaskbarStats.exe`""
```

## Atualizar

```powershell
Stop-Process -Name TaskbarStats -ErrorAction SilentlyContinue
cd $env:USERPROFILE\TaskbarStats
git pull
dotnet publish src -c Release -o "$env:LOCALAPPDATA\Programs\TaskbarStats"
Start-Process "$env:LOCALAPPDATA\Programs\TaskbarStats\TaskbarStats.exe"
```

## Desinstalar

```powershell
Stop-Process -Name TaskbarStats -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name TaskbarStats -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Programs\TaskbarStats", "$env:APPDATA\TaskbarStats" -ErrorAction SilentlyContinue
```

## Instalar com uma LLM (Claude Code, Copilot, Cursor etc.)

Copie e cole o texto abaixo no seu agente de IA com acesso ao terminal:

```text
Instale o TaskbarStats (https://github.com/gustavosouzalima/TaskbarStats) neste Windows 11.

Regras:
- Use PowerShell sem administrador. Não peça UAC.
- Não faça buscas pelo disco inteiro. Use apenas os caminhos abaixo.
- Mostre cada comando antes de rodar.

Passos:
1. Verifique `git --version` e `dotnet --list-sdks`. Se faltar Git, rode
   `winget install --id Git.Git -e`. Se não houver SDK 9.x, rode
   `winget install --id Microsoft.DotNet.SDK.9 -e`.
2. Clone em "$env:USERPROFILE\TaskbarStats" (se já existir, faça `git pull`).
3. Feche instâncias abertas: `Stop-Process -Name TaskbarStats -ErrorAction SilentlyContinue`.
4. Compile: `dotnet publish "$env:USERPROFILE\TaskbarStats\src" -c Release -o "$env:LOCALAPPDATA\Programs\TaskbarStats"`.
5. Registre a inicialização automática no valor "TaskbarStats" da chave
   HKCU:\Software\Microsoft\Windows\CurrentVersion\Run apontando para
   "$env:LOCALAPPDATA\Programs\TaskbarStats\TaskbarStats.exe" (entre aspas).
6. Inicie o exe e confirme com `Get-Process TaskbarStats` que está rodando.
7. Rode `dotnet build-server shutdown` para não deixar processos de build abertos.
8. Me diga onde ficou instalado e como desinstalar (seção "Desinstalar" do README).
```

## Como funciona

| Item | Fonte | Precisa de admin? |
|---|---|---|
| CPU | PDH `\Processor Information(_Total)\% Processor Utility` | não |
| RAM | `GlobalMemoryStatusEx` | não |
| GPU uso | PDH `\GPU Engine(*)\Utilization Percentage` (mesma fonte do Gerenciador) | não |
| GPU temp / VRAM | NVML (`C:\Windows\System32\nvml.dll`) | não |

A janela é uma *tool window* sem borda, sempre no topo, posicionada sobre a barra de tarefas. A cada segundo (mesmo intervalo do Gerenciador de Tarefas em velocidade normal) os sensores são lidos e a janela reafirma a posição no topo. A distância da borda direita da tela fica salva em `%APPDATA%\TaskbarStats\right-gap.txt`.

## Estrutura

```
src/
  Program.cs            janela, desenho, posição, menu
  Sensors.cs            CPU e GPU (PDH), RAM, temperatura/VRAM (NVML)
  TaskbarStats.csproj   .NET 9 WinForms
```
