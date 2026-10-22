import { Routes } from '@angular/router';
import { LauncherPage } from './pages/launcher/launcher.page';
import { RunPage } from './pages/run/run.page';

export const routes: Routes = [
  { path: '', component: LauncherPage, title: 'Agentic Console' },
  { path: 'runs/:id', component: RunPage, title: 'Run - Agentic Console' },
  { path: '**', redirectTo: '' },
];
