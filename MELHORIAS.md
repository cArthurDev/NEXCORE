# Melhorias de confiabilidade do NEXCORE

As alterações preservam as funções existentes e acrescentam serviços pequenos de persistência, execução de comandos, validação de bibliotecas, proteção térmica e coordenação de operações.

## Configurações e recuperação

- `settings.json` é gravado em arquivo temporário, sincronizado com o disco e substituído atomicamente. A versão anterior fica em `settings.json.bak`.
- Se o arquivo principal estiver corrompido, a cópia válida é recuperada e o arquivo danificado é preservado com sufixo `.corrupt-*`.
- Se nenhuma cópia puder ser lida, o aplicativo informa o erro em vez de apagar os backups dos ajustes do Windows.
- Na primeira execução sem configurações do NEXCORE, são importadas as configurações de `%AppData%\ResetExplode`. A pasta anterior permanece intacta, inclusive os atalhos de recuperação referenciados nela. Não apague essa pasta enquanto houver restaurações pendentes.
- A migração não substitui configurações já existentes do NEXCORE por uma versão antiga.

## Alterações no Windows

- A área de otimização possui limpeza separada de `%TEMP%` e `Windows\Temp`, com confirmação, cancelamento e contagem de arquivos e bytes removidos. As pastas principais são preservadas. Arquivos bloqueados, itens sem permissão e links/junctions são ignorados; caminhos protegidos são recusados antes de iniciar. A limpeza não é executada automaticamente junto da otimização de RAM.

- O estado original dos ajustes, reservas COM e entradas de inicialização é salvo antes de modificar o sistema.
- Restaurações parciais mantêm os backups dos itens que falharam, permitindo tentar novamente.
- As preferências de GPU preservam propriedades de outros aplicativos e restauram apenas a propriedade alterada pelo NEXCORE.
- Comandos possuem prazo de execução, leitura simultânea das saídas e validação do código de saída. Ajustes e restaurações executam fora da thread da janela.

## Testes e sensores

- Um coordenador permite apenas um teste de carga por vez: CPU, GPU, simulação, RAM, disco, ray tracing, rede ou verificação de estabilidade.
- A coleta de sensores executa em segundo plano. A interface recebe a última amostra disponível, sem acumular atualizações quando estiver ocupada.
- O corte térmico não depende da atualização visual. Ausência de leitura, valores inválidos e leituras atrasadas interrompem a carga de stress.
- O verificador retorna `INCONCLUSIVO` se não puder avaliar as temperaturas ou consultar os eventos do Windows. Um teste aprovado significa apenas que não houve erro detectado naquela execução.
- O diagnóstico de gargalo usa `Dados insuficientes` quando os sensores necessários não respondem.
- A captura ETW separa cadeias de apresentação quando o evento fornece o identificador e remove históricos inativos. A opção `1% low (p99)` informa o método utilizado; os eventos medem apresentações da aplicação, não confirmação de exibição de cada frame pelo monitor.
- O benchmark de disco usa um diretório exclusivo para não sobrescrever um `bench.tmp` preexistente.

## Permissões e bibliotecas

- O aplicativo inicia com a permissão do usuário. Recursos avançados podem oferecer a reabertura em modo administrador via UAC; a ação deve ser repetida na nova sessão.
- O modo administrador ainda é uma sessão completa do aplicativo, não um serviço privilegiado isolado. Sensores de baixo nível e algumas funções ETW dependem dessa sessão.
- Cada DLL externa da Corsair é validada antes do carregamento. Arquivos sem assinatura válida são recusados. Não há busca genérica pelo nome da DLL no PATH.
- Falhas recuperáveis são registradas em `%AppData%\NEXCORE\diagnostics.log`, com rotação de tamanho. Chaves de API e configurações completas não são registradas intencionalmente; revise o log antes de compartilhá-lo, pois erros do Windows podem incluir caminhos locais.

## Encerramento

O fechamento cancela testes, downloads e consultas, aguarda as tarefas ativas e só então libera sensores, overlay e integrações. Operações já entregues ao Windows, como uma instalação de driver pelo Windows Update, podem precisar terminar antes que a janela feche. O NEXCORE não promete desfazer instaladores externos já iniciados.

## Validação

Com um SDK .NET 8 instalado:

```powershell
dotnet build NEXCORE.csproj -c Release
dotnet run --project tests/NEXCORE.Tests.csproj
```

Neste workspace há um SDK local em `.tools/dotnet/dotnet.exe`, ignorado pelo Git. Ele também pode executar os comandos acima.

Os testes automatizados usam diretórios temporários e operações simuladas para cobrir gravação atômica, recuperação, migração, restauração parcial, preferências de GPU, exclusão de testes, cancelamento, prazos e proteção térmica. Não aplicam ajustes reais de registro, não instalam drivers e não executam carga de CPU/GPU.

A validação manual deve abranger abertura normal e elevada, cancelamento de cada teste, fechamento durante coleta/download, sensores de diferentes fabricantes, captura ETW em jogos e integração com SDK Corsair assinado. Esses cenários dependem do hardware e não são comprovados apenas pela compilação e pelos testes automatizados.
