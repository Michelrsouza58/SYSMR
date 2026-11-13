# SYSMR — Guia do Modo Offline (Rede Local)

Este guia explica como fazer o SYSMR funcionar **sem internet**: o computador do caixa vira um servidor local e os garçons usam o webapp pelo Wi-Fi da loja. Quando a internet voltar, tudo sincroniza automaticamente com a nuvem.

---

## 1. Como funciona

```
[ Celular do garçom ] ──Wi-Fi──► [ PC do caixa (SYSMR) ] ──► Impressoras (cozinha/bar)
                                        │
                                   SQLite local
                                        │
                        (quando a internet voltar) ▼
                                   Firebase (nuvem)
```

- O **SQLite local é sempre a fonte da verdade**. Tudo grava primeiro no computador do caixa.
- Pedidos feitos no modo local entram na fila de sincronização e sobem pro Firebase sozinhos quando a internet retornar.
- **Não existe "chaveamento"**: PDV, webapp local e impressão funcionam com ou sem internet, do mesmo jeito.

---

## 2. Preparando a rede (roteador)

A rede local **não precisa de internet** — o roteador só cria o Wi-Fi para os aparelhos conversarem entre si.

1. **Roteador dedicado (recomendado)**: um roteador só para a operação (PC do caixa + celulares dos garçons + impressoras de rede). Deixe o Wi-Fi dos clientes em outra rede.
2. **IP fixo para o PC do caixa** (o mais importante): no roteador, configure a **Reserva DHCP** amarrando o MAC address do PC a um IP fixo (ex.: `192.168.0.10`). Assim o endereço do webapp nunca muda.
3. **Senha WPA2/WPA3** no Wi-Fi da operação (nunca deixe aberta).
4. **QR Code**: gere um QR apontando para `http://192.168.0.10:8080` e deixe impresso no balcão. O garçom aponta a câmera e abre direto.

---

## 3. Ativando o servidor local no SYSMR

1. Abra o SYSMR → módulo **Restaurante** → **Configurações**.
2. Em **Servidor local (garçons no celular)**:
   - Marque **Ativar servidor local para os garçons**.
   - Mantenha a porta **8080** (ou outra entre 1024–65535).
   - Clique em **Salvar e aplicar**.
3. Clique em **Liberar porta no Firewall do Windows** (uma vez só; o Windows pedirá permissão de administrador).
4. A tela mostra os endereços para o celular, por exemplo:
   - `http://localhost:8080` (só funciona no próprio PC — para teste)
   - `http://192.168.0.10:8080` ← **este é o que o garçom usa**

> O servidor local sobe junto com o SYSMR. Se o SYSMR estiver aberto, o webapp está no ar — com ou sem internet.

### Detecção automática de queda de internet

O SYSMR verifica a internet a cada 15 segundos. Quando a conexão cai **e o servidor local está desligado**, aparece um banner amarelo **"📡 Sem internet"** no topo da tela com o botão **"Ativar servidor local"** — um clique liga o servidor e mostra o endereço para os garçons. Se o servidor já estiver ativo, o banner não aparece (a operação continua normal).

### QR Code para os garçons

Ao ativar o servidor local (pelo banner ou por Configurações), o SYSMR pergunta se você quer **imprimir um QR Code na impressora do balcão**. O cartão impresso contém:
- O QR Code apontando para o endereço do webapp (ex.: `http://192.168.0.10:8080`)
- O endereço escrito por extenso
- Instrução de conectar no Wi-Fi da loja

O garçom aponta a câmera do celular e abre direto. Para reimprimir depois, use o botão **"🖨 Imprimir QR Code para os garçons"** em Configurações → Servidor local. O QR Code é impresso com o comando nativo ESC/POS `GS ( k` (suportado por Epson, Elgin, Bematech, Daruma e clones POS80).

---

## 4. Instalando o webapp no servidor local

O webapp (React) precisa ser copiado para a pasta do servidor local **sempre que for atualizado**:

1. No projeto do webapp (`SYSMRPEdidos`), gere o build:
   ```powershell
   npm run build
   ```
2. Copie o conteúdo da pasta `dist` para:
   ```
   %LOCALAPPDATA%\SYSMR\waiter-app\
   ```
   (ou seja: `C:\Users\<seu-usuário>\AppData\Local\SYSMR\waiter-app\`)
3. Reinicie o SYSMR (ou desative e reative o servidor local em Configurações).

**Como saber se está servindo o webapp certo?** Abra `http://localhost:8080/api/status` no navegador do PC:
- `"webapp": "react"` → está servindo o webapp React ✅
- `"webapp": "embedded"` → está servindo a página simples embutida (a pasta `waiter-app` não foi encontrada)

> Se a pasta `waiter-app` não existir, o SYSMR serve automaticamente uma página simples embutida — o modo local continua funcionando, só que com a interface básica.

---

## 5. Cadastrando o garçom para o modo local

1. No SYSMR → **Funcionários** → cadastre ou edite o funcionário.
2. Preencha o **Número do garçom** (ex.: `01`) e o **PIN local** (4 a 12 dígitos).
3. Marque as **áreas** que ele pode atender.
4. Salve. **Não precisa de internet nem de senha web** — o PIN local já funciona.

> O PIN local fica gravado como hash seguro (PBKDF2) no SQLite. A senha web (Firebase) continua existindo para o acesso online, mas é opcional.

---

## 6. O garçom usando no celular

1. Conecte o celular no **Wi-Fi da loja** (o mesmo do PC do caixa).
2. Abra o endereço `http://192.168.0.10:8080` (ou o QR Code).
3. A tela de login mostra **"Modo local: conectado direto ao caixa"**.
4. Entre com **número do garçom + PIN local** (não precisa de código da loja nem senha).
5. Escolha a mesa, monte o pedido (com variações e observações) e envie.
6. O pedido:
   - aparece **na hora** na tela de mesas e na comanda aberta do desktop;
   - **imprime automaticamente** na cozinha (comidas) e no bar (bebidas), com o número do pedido;
   - fica marcado como "pendente de sincronização" e sobe pra nuvem quando a internet voltar.

---

## 7. E quando a internet voltar?

Nada precisa ser feito. O **Firebase Sync Worker** (que já roda em segundo plano no SYSMR):

1. Detecta que a rede voltou (health check a cada 20 segundos).
2. Envia todos os eventos pendentes (pedidos, itens, pagamentos) para o Firebase.
3. Marca cada registro como "Sincronizado".

O rodapé do SYSMR mostra o estado: **"SEM PENDÊNCIAS"** ou **"N EVENTO(S) PENDENTE(S)"**.

---

## 8. Solução de problemas

| Problema | O que verificar |
|---|---|
| Celular não abre o endereço | Está no mesmo Wi-Fi do PC? O firewall foi liberado (botão em Configurações)? O IP do PC mudou (configure a reserva DHCP)? |
| "Número ou PIN inválidos" | O funcionário tem PIN local cadastrado? Está ativo? O número está com o zero à esquerda? |
| Pedido não imprime | Impressora selecionada em Configurações → Impressoras? Veja o log em `%LOCALAPPDATA%\SYSMR\sysmr-debug.log`. |
| Mesa não atualiza no desktop | Atualize o SYSMR para a versão mais recente. O log mostra `[SyncWorker]` e `[Notifier]` quando um pedido chega. |
| Página simples em vez do webapp | A pasta `%LOCALAPPDATA%\SYSMR\waiter-app\` não tem o build. Refaça o passo 4. |
| Ícone do app sumiu no Windows | O ícone está embutido no exe; é o cache do Windows. Rode `ie4uinit.exe -show` no Prompt de Comando ou reinicie o Explorer. |

---

## 9. Resumo do checklist de implantação

- [ ] Roteador ligado (com ou sem internet) e Wi-Fi da operação com senha
- [ ] Reserva DHCP: PC do caixa com IP fixo
- [ ] SYSMR aberto → Configurações → Servidor local ativado + firewall liberado
- [ ] Webapp copiado para `%LOCALAPPDATA%\SYSMR\waiter-app\`
- [ ] Garçons cadastrados com número + PIN local + áreas
- [ ] Impressoras da cozinha e do bar selecionadas em Configurações
- [ ] QR Code do endereço impresso no balcão
- [ ] Teste: pedido pelo celular → aparece no desktop → imprime na cozinha

## 10. Desvincular uma loja deste computador

Na tela de login, use **Desvincular loja** e autorize com o Super Admin.
Escolher **Sim** remove do banco local os cadastros, garçons, comandas, caixa,
vendas, histórico e eventos de sincronização pendentes. Faça um backup do banco
`%LOCALAPPDATA%\SYSMR\sysmr.db` antes da operação, com o SYSMR fechado, e
confira se existem pendências que ainda precisam ser sincronizadas. Após apagar,
elas não poderão ser enviadas a partir deste computador.

Essa operação **não apaga dados nem contas no Firebase**. Ela preserva a
configuração do Firebase, as impressoras, o Super Admin e outras configurações
do computador, além de registrar a desvinculação na auditoria. Dados do
estabelecimento nos recibos e a numeração de pedidos são limpos. Ao entrar no
módulo restaurante, o salão e as mesas padrão são recriados para a nova loja.
Escolher **Não** desvincula a conta sem limpar os dados, que continuarão
visíveis para a próxima loja.
