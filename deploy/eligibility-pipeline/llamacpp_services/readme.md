These changes are needed to deploy the services:

1. The **LLAMA_ROOT **environment variable must be set at Machine scope on the target host before
running the redeploy script so the service account (LocalSystem)
sees it; You must start a new shell before deploying and after setting the environment variable
**[Environment]::SetEnvironmentVariable('LLAMA_ROOT', 'E:', 'Machine')**
1. Download **WinSW **for your architecture from https://github.com/winsw/winsw/releases , make three copies and name them **llama-main.exe**, **llama-normalize.exe**,** llama-embedding.exe**
1. Ensure your models are downloaded and linked from the .xml files
   - **Embedding Model**: sabafallah\bge-large-en-v1.5-q8_0.gguf
   - **Normalization Model**: bartowski\allura-forge_Llama-3.3-8B-Instruct-Q4_K_S.gguf
   - **Main Model**: lmstudio-community\gemma-4-26B-A4B-it-Q8_0.gguf
1. Ensure llama cpp executables are available in the folder linked to in the xmls



