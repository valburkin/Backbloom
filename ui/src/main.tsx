import { StrictMode, useState } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource-variable/inter';
import '@fontsource-variable/space-grotesk';
import './index.css';
import { createBackend } from './api/backend';
import { ConnectionProvider } from './app/connection';
import { ConfigProvider } from './app/config';
import { EditorProvider } from './app/editor';
import { ToastProvider } from './app/toast';
import { App } from './app/App';

function Root() {
  const [backend] = useState(createBackend);
  return (
    <ToastProvider>
      <ConnectionProvider backend={backend}>
        <ConfigProvider>
          <EditorProvider>
            <App />
          </EditorProvider>
        </ConfigProvider>
      </ConnectionProvider>
    </ToastProvider>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Root />
  </StrictMode>,
);
