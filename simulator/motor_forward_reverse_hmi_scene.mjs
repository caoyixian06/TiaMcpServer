import { Checker } from './lad-sim.mjs';

export async function run(sim, { Checker: CheckerType = Checker } = {}) {
  sim.enableTrace(['HMI_正转启动', 'HMI_反转启动', 'HMI_停止', 'PLC_正转保持', 'PLC_反转保持', 'PLC_正转输出', 'PLC_反转输出', 'PLC_故障']);
  const ck = new CheckerType(sim);

  sim.advance(2);
  ck.bool('PLC_正转输出', false, '上电安全状态');
  ck.bool('PLC_反转输出', false, '上电安全状态');

  sim.advance(2, [{ at: 0, set: { 'HMI_正转启动': true } }, { at: 1, set: { 'HMI_正转启动': false } }]);
  ck.bool('PLC_正转输出', true, 'HMI正转启动后正转输出');
  ck.bool('PLC_反转输出', false, '正转时反转输出关闭');

  sim.advance(2, [{ at: 0, set: { 'HMI_反转启动': true } }, { at: 1, set: { 'HMI_反转启动': false } }]);
  ck.bool('PLC_正转输出', true, '正转保持时反转命令被互锁');
  ck.bool('PLC_反转输出', false, '正转保持时反转命令被互锁');

  sim.advance(2, [{ at: 0, set: { 'HMI_停止': true } }, { at: 1, set: { 'HMI_停止': false, 'HMI_正转启动': false } }]);
  ck.bool('PLC_正转输出', false, '停止后正转输出关闭');

  sim.advance(2, [{ at: 0, set: { 'HMI_反转启动': true } }, { at: 1, set: { 'HMI_反转启动': false } }]);
  ck.bool('PLC_反转输出', true, '停止后反转启动');
  ck.bool('PLC_正转输出', false, '反转时正转输出关闭');

  sim.advance(2, [{ at: 0, set: { 'HMI_急停': true } }]);
  ck.bool('PLC_正转输出', false, '急停优先关闭正转');
  ck.bool('PLC_反转输出', false, '急停优先关闭反转');
  ck.bool('PLC_故障', true, '急停产生故障状态');

  sim.advance(2, [{ at: 0, set: { 'HMI_急停': false, 'HMI_故障复位': true } }, { at: 1, set: { 'HMI_故障复位': false } }]);
  ck.bool('PLC_正转输出', false, '故障复位不自动重启');
  ck.bool('PLC_反转输出', false, '故障复位不自动重启');

  sim.advance(2);
  ck.assertNoUnresolved();
  return ck.summary();
}
