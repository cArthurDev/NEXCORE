# Instalador do NEXCORE

Execute `criar-instalador.bat` na raiz do projeto. O script publica o código atual e gera:

- `installer/output/NEXCORE-Setup.exe`: arquivo único para enviar aos usuários, com o MSI e o .NET incluídos.
- `installer/output/NEXCORE-0.16.0-x64.msi`: pacote Windows Installer alternativo. O número acompanha a versão do projeto.
- `installer/output/NEXCORE-Setup.sha256`: hash SHA-256 do executável gerado.

O Setup apresenta a licença em uma interface em português, instala em `Arquivos de Programas\NEXCORE`, cria atalhos na área de trabalho e no menu Iniciar e oferece reparação e desinstalação. O publicador nos metadados é **cArthurDev (Arthur Souza)**. O ícone vem de `Assets/app.ico`.

Configurações, capas e backups do usuário em `%AppData%\NEXCORE` permanecem fora do instalador e não são removidos na desinstalação. Os créditos originais da licença MIT são mantidos. Um arquivo com os avisos das dependências acompanha a instalação.

Para gerar versões futuras, atualize `<Version>` em `NEXCORE.csproj` e execute o script novamente. Mantenha os dois `UpgradeCode` dos arquivos `.wxs`; eles permitem identificar e substituir versões anteriores. Use uma versão maior ao distribuir uma atualização.

A compilação usa o SDK .NET 8 local em `.tools/dotnet`, ou o SDK disponível no sistema, e prepara o WiX 5.0.2 em `.tools/wix`. A primeira preparação das ferramentas e das dependências precisa de internet. O instalador gerado não baixa o .NET durante a instalação.

O instalador não instala automaticamente PawnIO nem outros drivers: as funções correspondentes continuam disponíveis dentro do aplicativo. O executável não possui assinatura Authenticode; o nome do autor nos metadados não substitui um certificado de assinatura.

A validação do pacote verifica conteúdo, ícone, autoria, licença, atalhos e hash do aplicativo embarcado por extração, sem instalar o NEXCORE no computador de desenvolvimento. Instalação completa, atualização e remoção devem ser verificadas em uma máquina de teste ou VM antes de uma distribuição pública ampla.
