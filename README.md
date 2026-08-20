## Experiments with Agent Framework/Blazor/Aspire/AG-UI with server managed chat history

### The project is an experiment with this combination of technologies:

- Microsoft Agent Framework
- Azure/Foundry
- Aspire
- Blazor front end
- AG-UI : Principally for client side tool calling, opening up the ability for the agent to trigger custom UI
- Agent Skills (Foundry)
- Agent Memory (Foundry)
- Server managed chat history (client only sends user message, chat history is constructed on the back end)
  - Initially attempted with foundry managed conversation history but more custom implementation needed to work with AG-UI flow (cosmos backed).
  - AG-UI doesn't play with conversation id's, it has a concept of thread id's which superficially seem the same, but custom implementation is needed to tie things together.

Goals are to have a Blazor Chat UI in which you can 
 - create/list/resume conversations
 - Trigger some front end tool call (in this case, ask it 'Where am I?' to get it to use geolocation on the client)
 - Trigger use of a skill (in this case 'silly math', i.e. 'use your silly math skill to do 1 + 1)

I don't claim that any approach used in this project is a good idea, but I'm happy with what I've got working so far in terms of the goal of having AG-UI without needing the whole chat history sent by the client. AG-UI doesn't seem want to play ball with server managed conversation history if you try and use things the 'out of the box/getting started' way with Agent Framework/Foundry and this is what I've put together (what co-pilot has put together with my input), to make it work.

It's the custom chat history provider doing a lot of the heavy lifting on this, as the way AG-UI calls/turns take shape, you end up with very strange out of shape history 
with much duplication and out of order messages if you try to do this with the CosmosChatHistoryProvider on nuget.

### To run:

You'll need your own Azure subscription details plugged into user secrets on the Aspire project. Perhaps it prompts you, but if not:

"Azure:SubscriptionId"
"Azure:ResourceGroup"
"Azure:Location"

Are what you need.

You can run the integration tests to get a sense of things working, as well as use the Blazor UI.

For client side tool call, ask "Where am I?"
For a skill call, ask "use your silly math skill to add 1 + 1"
