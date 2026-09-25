# ToDoNotes document

I write here some notes about what to implement next.

# TODO:

- [ ] [Architecture] Fugure out how executing of recurring tasks works under load with many users (e.g. will it be able to run 100 paralel tasks from many different users, won't a schduled task be skipped because of intensive commands or other parallel tasks, will the func will be autoscaled under the load, what is scalability limit?)

- [ ] [Security] How does authenticaion works between Telegram and Function App? is it secure? Can we improve it?


- [ ] [Architecture] Perform refactoring of project structure to follow `Vertical Slices` and `Clean Architecture`(optional) to ease future development of the project. Separate feature flices, separate Persistence, LLM, TelegramBot layers.


- [ ] [Documentation] Perform deep refactoring of the documentation. I need structured documentation per layers / concerns, e.g. Persistance layer, API layers, LLM layer, command exectution flow, deployment, CI / testing, runbook, setup, etc. Keep the spec.Phase# as historical documents.

- [ ] [UI/UX] Output long responses as markdown files and attach them to messages. In the message write only a short summary.


- [ ] [UI/UX] Build a user-friencly UI (web based) to set recurrent task in UI. Investigate cheap or free hosting for the UI in Azure. Consider building a second Function App for performing backend operations for the UI but design it for minimal calling the backend (e.g. only for authentication), to minimize billing for the Function App
  